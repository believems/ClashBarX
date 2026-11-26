using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;
using ClashBarX.Core.Proxies;

namespace ClashBarX.Core.ProxyGuard;

/// <summary>最近一次探测结果（源：ProxyGuardProbeOutcome——供设置页反馈行显示）。</summary>
public enum ProxyGuardProbeOutcome
{
    Reachable,
    Failed,
}

public sealed record ProxyGuardProbeState(ProxyGuardProbeOutcome Outcome, int ConsecutiveFailures);

/// <summary>
/// 自动代理守护（源：AppViewModel+ProxyGuard.swift 逐行对照）。
/// 周期性经当前混合端口探测外网连通性，连续 3 次失败后按「组内切换 → 订阅自愈」恢复：
/// 组内切换 = 对每个 Selector 组测速，当前节点不可用而组内有可用节点时切换到最快；
/// 全部组无可用节点时更新全部代理提供者（订阅自愈），3 秒后重测再切换。
/// 探测/延迟/组测速均注入可测；事件回调来自后台线程，UI 侧自行编组。
/// </summary>
public sealed class ProxyGuardService : IDisposable
{
    public const int FailureThreshold = 3;
    public const string DefaultProbeUrl = "https://www.gstatic.com/generate_204";
    public const int MinimumIntervalSeconds = 30;
    public const int DefaultHealthcheckTimeoutMilliseconds = 5000;

    /// <summary>CLI 触发参数（源：cliTriggerArgument = "--proxy-guard"）。</summary>
    public const string CliTriggerArgument = "--proxy-guard";

    /// <summary>订阅自愈后等待内核刷新的时间（源：3_000_000_000ns）。</summary>
    public static readonly TimeSpan ProviderRefreshWait = TimeSpan.FromSeconds(3);

    private readonly IMihomoApiClient _client;
    private readonly Func<CancellationToken, Task<bool>> _probeAsync;
    private readonly Func<int> _intervalSecondsProvider;
    private readonly Func<bool> _canProbe;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly object _gate = new();
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private int _consecutiveFailures;
    private bool _recoveryInFlight;

    public ProxyGuardService(
        IMihomoApiClient client,
        Func<CancellationToken, Task<bool>> probeAsync,
        Func<int> intervalSecondsProvider,
        Func<bool> canProbe,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = client;
        _probeAsync = probeAsync;
        _intervalSecondsProvider = intervalSecondsProvider;
        _canProbe = canProbe;
        _delay = delay ?? DefaultDelay;
    }

    /// <summary>探测结束（含恢复流程的复测）；UI 反馈行与计数展示用。</summary>
    public event Action<ProxyGuardProbeState>? ProbeCompleted;

    /// <summary>守护日志（level: info/warning/error；源：appendLog 系列）。</summary>
    public event Action<string, string>? Logged;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public bool IsProbing { get; private set; }

    /// <summary>失败计数快照（设置页反馈行）。</summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_gate)
            {
                return _consecutiveFailures;
            }
        }
    }

    /// <summary>
    /// 启动守护循环（幂等；源：startProxyGuardIfNeeded）。<paramref name="probeImmediately"/>
    /// 为 CLI 触发的首启语义：先立即探一次再进入周期循环。
    /// </summary>
    public void Start(bool probeImmediately = false)
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                return;
            }

            _consecutiveFailures = 0;
            _loopCancellation = new CancellationTokenSource();
            CancellationToken token = _loopCancellation.Token;
            _loopTask = Task.Run(() => GuardLoopAsync(probeImmediately, token), token);
        }

        Logged?.Invoke("info", "代理守护已启动");
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_loopCancellation is null)
            {
                return;
            }

            _loopCancellation.Cancel();
            _loopCancellation.Dispose();
            _loopCancellation = null;
            _loopTask = null;
            _consecutiveFailures = 0;
        }
    }

    /// <summary>外部触发的立即探测（「立即检查」按钮与 CLI 命名事件；源：分布式通知分支）。</summary>
    public async Task<ProxyGuardProbeState> ProbeNowAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return new ProxyGuardProbeState(ProxyGuardProbeOutcome.Failed, ConsecutiveFailures);
        }

        return await ProbeCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task GuardLoopAsync(bool probeImmediately, CancellationToken token)
    {
        if (probeImmediately)
        {
            await ProbeCoreAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                return;
            }
        }

        while (!token.IsCancellationRequested)
        {
            // 周期下限 30s，每轮重读（源：max(30, intervalSeconds) 的动态语义）
            int interval = Math.Max(MinimumIntervalSeconds, _intervalSecondsProvider());
            try
            {
                await _delay(TimeSpan.FromSeconds(interval), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            await ProbeCoreAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>一次探测并推进失败计数（源：proxyGuardProbe——含恢复触发）。</summary>
    private async Task<ProxyGuardProbeState> ProbeCoreAsync(CancellationToken token)
    {
        if (!_canProbe())
        {
            return new ProxyGuardProbeState(ProxyGuardProbeOutcome.Failed, ConsecutiveFailures);
        }

        IsProbing = true;
        try
        {
            bool reachable = await _probeAsync(token).ConfigureAwait(false);
            int failures;
            lock (_gate)
            {
                if (reachable)
                {
                    if (_consecutiveFailures > 0)
                    {
                        Logged?.Invoke("info", "网络连通已恢复");
                    }

                    _consecutiveFailures = 0;
                }
                else
                {
                    _consecutiveFailures++;
                    Logged?.Invoke("warning", $"探测失败（连续第 {_consecutiveFailures} 次，阈值 {FailureThreshold}）");
                }

                failures = _consecutiveFailures;
            }

            var state = new ProxyGuardProbeState(
                reachable ? ProxyGuardProbeOutcome.Reachable : ProxyGuardProbeOutcome.Failed,
                reachable ? 0 : failures);
            ProbeCompleted?.Invoke(state);
            if (reachable)
            {
                return state;
            }

            if (failures >= FailureThreshold)
            {
                lock (_gate)
                {
                    _consecutiveFailures = 0;   // 恢复流程清零（源：98-101 行）
                }

                await RecoverAsync(token).ConfigureAwait(false);
            }

            return state;
        }
        finally
        {
            IsProbing = false;
        }
    }

    /// <summary>恢复：组内切换 → 复测 → 订阅自愈 → 3 秒重测再切换（源：proxyGuardRecover）。</summary>
    private async Task RecoverAsync(CancellationToken token)
    {
        lock (_gate)
        {
            if (_recoveryInFlight)
            {
                return;
            }

            _recoveryInFlight = true;
        }

        try
        {
            Logged?.Invoke("warning", "连续失败达到阈值，开始恢复（组内切换 → 订阅自愈）");

            // 1) 组内切换：当前节点坏、组内有活节点就直接切
            if (await RecoverBySwitchingWithinGroupsAsync(token).ConfigureAwait(false)
                && await _probeAsync(token).ConfigureAwait(false))
            {
                Logged?.Invoke("info", "网络连通已恢复");
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            // 2) 订阅自愈：更新全部提供者，等内核刷新后重测再切换
            await UpdateAllProxyProvidersAsync(token).ConfigureAwait(false);
            try
            {
                await _delay(ProviderRefreshWait, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (await RecoverBySwitchingWithinGroupsAsync(token).ConfigureAwait(false)
                && await _probeAsync(token).ConfigureAwait(false))
            {
                Logged?.Invoke("info", "网络连通已恢复");
            }
            else
            {
                Logged?.Invoke("error", "恢复失败：组内无可用节点且订阅自愈未生效");
            }
        }
        finally
        {
            lock (_gate)
            {
                _recoveryInFlight = false;
            }
        }
    }

    /// <summary>
    /// 对每个 Selector 组测速：当前节点可用则跳过，否则切到组内最快可用节点（源：recoverBySwitchingWithinGroups）。
    /// 返回是否发生过切换。
    /// </summary>
    private async Task<bool> RecoverBySwitchingWithinGroupsAsync(CancellationToken token)
    {
        bool didSwitch = false;

        ProxyGroupsResponse response = await _client
            .RequestAsync<ProxyGroupsResponse>(MihomoEndpoint.Proxies(), token)
            .ConfigureAwait(false);

        IEnumerable<ProxyGroup> selectorGroups = response.Proxies.Values
            .Where(proxy => proxy.All.Count > 0
                && (proxy.Type ?? string.Empty).ToLowerInvariant().Contains("selector"));

        foreach (ProxyGroup group in selectorGroups)
        {
            GroupDelayMeasurement? results = await MeasureGroupDelaysAsync(group, token).ConfigureAwait(false);
            if (results is null)
            {
                continue;
            }

            string? current = group.Now;
            if (string.IsNullOrEmpty(current))
            {
                continue;
            }

            // 当前节点测速有效（含正延迟）则不动（源：results[current].map { $0 > 0 } == true）
            if (results.Values.TryGetValue(current, out int currentDelay) && currentDelay > 0)
            {
                continue;
            }

            // 组内最快可用节点（源：filter { $0.value > 0 && $0.key != current }.min(by: delay)）
            KeyValuePair<string, int>? best = results.Values
                .Where(pair => pair.Value > 0 && pair.Key != current)
                .OrderBy(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Cast<KeyValuePair<string, int>?>()
                .FirstOrDefault();
            if (best is null)
            {
                continue;
            }

            await _client.RequestNoResponseAsync(
                MihomoEndpoint.SwitchProxy(group.Name, best.Value.Key), token).ConfigureAwait(false);
            Logged?.Invoke("info", $"已切换 {group.Name} → {best.Value.Key}（{best.Value.Value} ms）");
            didSwitch = true;
        }

        return didSwitch;
    }

    /// <summary>组测速（源：measureGroupDelays——直接请求内核，不经 UI 延迟状态）。</summary>
    private async Task<GroupDelayMeasurement?> MeasureGroupDelaysAsync(ProxyGroup group, CancellationToken token)
    {
        string testUrl = group.TestUrl ?? DefaultProbeUrl;
        int timeout = group.Timeout is int value and > 0 ? value : DefaultHealthcheckTimeoutMilliseconds;
        try
        {
            return await _client.RequestAsync<GroupDelayMeasurement>(
                MihomoEndpoint.GroupDelay(group.Name, testUrl, timeout), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;   // 组测速失败跳过该组（源：catch { return nil }）
        }
    }

    /// <summary>更新全部代理提供者（订阅自愈；源：updateAllProxyProvidersForRecovery）。</summary>
    private async Task UpdateAllProxyProvidersAsync(CancellationToken token)
    {
        ProviderSummary? providers;
        try
        {
            providers = await _client.RequestAsync<ProviderSummary>(
                MihomoEndpoint.ProxyProviders(), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logged?.Invoke("warning", $"拉取代理提供者失败：{error.Message}");
            return;
        }

        List<string> names = providers.Providers.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return;
        }

        Logged?.Invoke("info", $"更新全部订阅自愈（{names.Count} 个提供者）");
        foreach (string name in names)
        {
            try
            {
                await _client.RequestNoResponseAsync(
                    MihomoEndpoint.UpdateProxyProvider(name), token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Logged?.Invoke("warning", $"更新提供者 {name} 失败：{error.Message}");
            }
        }
    }

    private static Task DefaultDelay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);

    public void Dispose()
    {
        Stop();
    }
}
