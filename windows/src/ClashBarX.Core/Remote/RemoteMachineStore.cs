using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashBarX.Core.Remote;

/// <summary>持久化载荷（源：storageKey + activeTargetKey 两个 defaults 键的文件化对应物）。</summary>
internal sealed record RemoteMachineStorage(
    [property: JsonPropertyName("machines")] IReadOnlyList<RemoteMachine> Machines,
    [property: JsonPropertyName("activeTargetId")] string? ActiveTargetId)
{
    public static RemoteMachineStorage Empty { get; } = new(
        Array.Empty<RemoteMachine>(), null);
}

/// <summary>
/// 远程机器仓库（源：Stores/RemoteMachineStore.swift）：增删改、持久化
/// （remote-machines.json）、连通性探测（GET {base}/version，1s 超时 + Bearer）、
/// 5s 周期检查与 generation 防竞态。方法约定在 UI 线程调用；
/// StatusChanged 可能从探测线程发出，UI 编组由订阅方承担。
/// </summary>
public sealed class RemoteMachineStore : IDisposable
{
    /// <summary>探测超时（源：probe 的 timeoutInterval = 1s）。</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    /// <summary>周期检查间隔（源：startPeriodicConnectivityChecks 的 5s）。</summary>
    public static readonly TimeSpan PeriodicCheckInterval = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _storagePath;
    private readonly HttpClient _client;
    private readonly object _gate = new();

    private List<RemoteMachine> _machines = new();
    private Guid? _activeMachineId;
    private readonly Dictionary<Guid, MachineConnectionStatus> _statuses = new();
    private readonly Dictionary<Guid, (int Generation, CancellationTokenSource Cancellation)> _probes = new();
    private int _nextProbeGeneration;
    private System.Threading.Timer? _connectivityTimer;

    /// <summary>机器列表或激活目标变化（增删改/切换）。</summary>
    public event Action? MachinesChanged;

    /// <summary>探测状态变化（可能从后台线程发出）。</summary>
    public event Action<Guid>? StatusChanged;

    public RemoteMachineStore(string? storagePath = null, HttpMessageHandler? httpMessageHandler = null)
    {
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClashBarX", "remote-machines.json");
        _client = new HttpClient(httpMessageHandler ?? new HttpClientHandler());
        Load();
    }

    public IReadOnlyList<RemoteMachine> Machines
    {
        get
        {
            lock (_gate)
            {
                return _machines.ToList();
            }
        }
    }

    public Guid? ActiveMachineId
    {
        get
        {
            lock (_gate)
            {
                return _activeMachineId;
            }
        }
    }

    /// <summary>当前激活机器（激活 id 无对应机器时为 null）。</summary>
    public RemoteMachine? ActiveMachine
    {
        get
        {
            lock (_gate)
            {
                return _activeMachineId is { } id ? _machines.FirstOrDefault(machine => machine.Id == id) : null;
            }
        }
    }

    /// <summary>当前目标是否远程（源：isRemoteTarget）。</summary>
    public bool IsRemoteTarget => ActiveMachine is not null;

    public void AddMachine(RemoteMachine machine)
    {
        lock (_gate)
        {
            _machines.Add(machine);
        }

        Persist();
        MachinesChanged?.Invoke();
    }

    public void UpdateMachine(RemoteMachine machine)
    {
        lock (_gate)
        {
            int index = _machines.FindIndex(item => item.Id == machine.Id);
            if (index < 0)
            {
                return;
            }

            CancelProbeLocked(machine.Id);
            _machines[index] = machine;
            _statuses[machine.Id] = MachineConnectionStatus.Unknown;
        }

        Persist();
        MachinesChanged?.Invoke();
    }

    public void RemoveMachine(Guid id)
    {
        lock (_gate)
        {
            CancelProbeLocked(id);
            _statuses.Remove(id);
            _machines.RemoveAll(machine => machine.Id == id);
            if (_activeMachineId == id)
            {
                _activeMachineId = null;
            }
        }

        Persist();
        MachinesChanged?.Invoke();
    }

    /// <summary>切换目标（null = 本机；源：selectTarget/resetActiveTarget）。</summary>
    public void SelectTarget(Guid? machineId)
    {
        lock (_gate)
        {
            _activeMachineId = machineId;
        }

        Persist();
        MachinesChanged?.Invoke();
    }

    public MachineConnectionStatus StatusOf(Guid id)
    {
        lock (_gate)
        {
            return _statuses.GetValueOrDefault(id) ?? MachineConnectionStatus.Unknown;
        }
    }

    public void CheckAllConnectivity()
    {
        foreach (RemoteMachine machine in Machines)
        {
            _ = StartProbeAsync(machine);
        }
    }

    public Task<MachineConnectionStatus> RefreshConnectivityAsync(RemoteMachine machine) =>
        StartProbeAsync(machine);

    /// <summary>启动周期检查（源：startPeriodicConnectivityChecks——先全量一次再 5s 循环）。</summary>
    public void StartPeriodicChecks()
    {
        StopPeriodicChecks();
        CheckAllConnectivity();
        lock (_gate)
        {
            _connectivityTimer = new System.Threading.Timer(
                _ => CheckAllConnectivity(), null, PeriodicCheckInterval, PeriodicCheckInterval);
        }
    }

    public void StopPeriodicChecks()
    {
        lock (_gate)
        {
            _connectivityTimer?.Dispose();
            _connectivityTimer = null;
            foreach ((_, var cancellation) in _probes.Values)
            {
                cancellation.Cancel();
            }

            _probes.Clear();
        }
    }

    public void Dispose()
    {
        StopPeriodicChecks();
        _client.Dispose();
    }

    /// <summary>
    /// 单机探测（源：startProbe）：旧探测取消 + generation 递增防旧结果覆盖，
    /// 完成（且仍是当前代）时更新状态并发事件。
    /// </summary>
    private async Task<MachineConnectionStatus> StartProbeAsync(RemoteMachine machine)
    {
        (int generation, CancellationTokenSource cancellation, bool exists) = BeginProbe(machine.Id);
        if (!exists)
        {
            cancellation.Dispose();
            return MachineConnectionStatus.Unknown;
        }

        StatusChanged?.Invoke(machine.Id);   // Checking 态（lock 外发）

        MachineConnectionStatus status;
        try
        {
            status = await ProbeAsync(machine, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
        }

        lock (_gate)
        {
            if (_probes.TryGetValue(machine.Id, out (int Generation, CancellationTokenSource _) probe)
                && probe.Generation == generation)
            {
                _probes.Remove(machine.Id);
            }
            else
            {
                return status;   // 旧代结果或已被新一轮取代，不覆盖状态
            }

            if (!_machines.Contains(machine))
            {
                return status;   // 机器已被删除
            }

            _statuses[machine.Id] = status;
        }

        StatusChanged?.Invoke(machine.Id);
        return status;
    }

    private (int Generation, CancellationTokenSource Cancellation, bool Exists) BeginProbe(Guid id)
    {
        lock (_gate)
        {
            if (_machines.All(machine => machine.Id != id))
            {
                return (0, new CancellationTokenSource(), false);
            }

            CancelProbeLocked(id);
            int generation = ++_nextProbeGeneration;
            var cancellation = new CancellationTokenSource();
            _statuses[id] = MachineConnectionStatus.Checking;
            _probes[id] = (generation, cancellation);
            return (generation, cancellation, true);
        }
    }

    private void CancelProbeLocked(Guid id)
    {
        if (_probes.Remove(id, out (int _, CancellationTokenSource cancellation) probe))
        {
            probe.cancellation.Cancel();
            probe.cancellation.Dispose();
        }
    }

    /// <summary>HTTP 探测（源：probe——URL 兜底 scheme、Bearer、状态码语义、错误归因）。</summary>
    private async Task<MachineConnectionStatus> ProbeAsync(RemoteMachine machine, CancellationToken cancellationToken)
    {
        string address = machine.ControllerAddress;
        string baseAddress = address.Contains("://", StringComparison.Ordinal) ? address : $"http://{address}";
        if (!Uri.TryCreate($"{baseAddress}/version", UriKind.Absolute, out Uri? url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return MachineConnectionStatus.Failed("Invalid URL");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(machine.Secret))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", machine.Secret);
            }

            using HttpResponseMessage response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return MachineConnectionStatus.Failed("Auth failed (401)");
            }

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return MachineConnectionStatus.Failed($"HTTP {(int)response.StatusCode}");
            }

            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("version", out JsonElement version)
                    && version.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(version.GetString()))
                {
                    return MachineConnectionStatus.Connected(version.GetString()!);
                }
            }
            catch (JsonException)
            {
                // 无效 JSON 按无版本处理（源：json 解析失败 → connected("OK")）
            }

            return MachineConnectionStatus.Connected("OK");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MachineConnectionStatus.Unknown;   // 源：cancelled → unknown
        }
        catch (OperationCanceledException)
        {
            return MachineConnectionStatus.Failed("Timeout");
        }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.ConnectionError)
        {
            return MachineConnectionStatus.Failed("Connection refused");
        }
        catch (HttpRequestException)
        {
            return MachineConnectionStatus.Failed("Connection lost");
        }
        catch (Exception error)
        {
            return MachineConnectionStatus.Failed(error.Message);
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_storagePath))
                {
                    RemoteMachineStorage? storage = JsonSerializer
                        .Deserialize<RemoteMachineStorage>(File.ReadAllText(_storagePath), JsonOptions);
                    if (storage != null)
                    {
                        _machines = storage.Machines?.ToList() ?? new List<RemoteMachine>();
                        _activeMachineId = storage.ActiveTargetId is { } text
                            && Guid.TryParse(text, out Guid id)
                            ? id
                            : null;
                        return;
                    }
                }
            }
            catch (Exception error) when (error is JsonException or IOException)
            {
                // 损坏文件按空库处理（源：decode 失败返回 []）
            }

            _machines = new List<RemoteMachine>();
        }
    }

    private void Persist()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
                var storage = new RemoteMachineStorage(
                    _machines,
                    _activeMachineId?.ToString());
                File.WriteAllText(_storagePath, JsonSerializer.Serialize(storage, JsonOptions));
            }
            catch (IOException)
            {
                // 持久化失败静默（源：NSLog 后继续运行）
            }
        }
    }
}
