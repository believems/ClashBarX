using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;

namespace ClashBarX.Core.Runtime;

/// <summary>API 健康状态（源：Runtime.swift 的 APIHealth）。</summary>
public enum ApiHealth
{
    Unknown,
    Healthy,
    Degraded,
    Failed,
}

/// <summary>
/// API 就绪轮询（源：AppViewModel+Polling.swift 的 runRefresh 状态机 + startPeriodicTask 循环）：
/// 周期探 GET /version；成功 → Healthy；失败 → Degraded 并按错误指纹去重记录；
/// 间隔由 intervalProvider 每轮动态决定（源：degraded 时提速至 1s、面板开合切换前后台间隔）。
/// 循环在线程池上运行，回调可能来自后台线程，UI 层需自行编组。
/// </summary>
public sealed class ApiStatusPoller : IDisposable
{
    private static readonly Func<TimeSpan> DefaultInterval = static () => TimeSpan.FromSeconds(4);

    private static readonly Func<TimeSpan, CancellationToken, Task> DefaultDelay =
        static (span, token) => Task.Delay(span, token);

    private readonly IMihomoApiClient _client;
    private readonly Func<TimeSpan> _intervalProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cancellation = new();

    private Task _loopTask = Task.CompletedTask;
    private ApiHealth _status = ApiHealth.Unknown;
    private string? _lastErrorFingerprint;
    private bool _started;

    /// <summary>状态迁移时触发（同一状态不重复触发，源：if apiStatus != .healthy 才赋值）。</summary>
    public event Action<ApiHealth>? StatusChanged;

    /// <summary>错误日志（同一类型+消息只记一次，源：lastAPIRefreshErrorFingerprint 去重）。</summary>
    public event Action<string>? ErrorLogged;

    /// <summary>探测成功的内核版本号。</summary>
    public event Action<string>? VersionChanged;

    public ApiHealth Status
    {
        get { lock (_lock) return _status; }
    }

    public string? Version
    {
        get { lock (_lock) return _version; }
        private set { lock (_lock) _version = value; }
    }

    private string? _version;

    public ApiStatusPoller(
        IMihomoApiClient client,
        Func<TimeSpan>? intervalProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = client;
        _intervalProvider = intervalProvider ?? DefaultInterval;
        _delay = delay ?? DefaultDelay;
    }

    /// <summary>启动轮询（幂等）。循环在线程池上运行，与调用方同步上下文解耦。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _loopTask = Task.Run(() => LoopAsync(_cancellation.Token));
    }

    /// <summary>停止轮询（幂等）。</summary>
    public void Stop() => Dispose();

    public void Dispose()
    {
        Task loop;
        lock (_lock)
        {
            if (!_started)
            {
                _cancellation.Dispose();
                return;
            }
        }

        _cancellation.Cancel();
        loop = _loopTask;
        try
        {
            // 循环退出前的最后一次延迟最多等待一个间隔，避免 Dispose 长时间阻塞
            loop.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // 循环的意外异常不阻塞销毁
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _delay(_intervalProvider(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>单次探测与状态迁移（源：runRefresh 的 do/catch 结构）。</summary>
    internal async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            VersionInfo version = await _client
                .RequestAsync<VersionInfo>(MihomoEndpoint.Version(), cancellationToken)
                .ConfigureAwait(false);

            Version = version.Version;
            VersionChanged?.Invoke(version.Version);
            SetStatus(ApiHealth.Healthy);
            _lastErrorFingerprint = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            SetStatus(ApiHealth.Degraded);
            // 源：指纹 = "\(类型):\(消息)"，同一指纹不重复记日志；成功后指纹清空
            string fingerprint = $"{error.GetType().FullName}:{error.Message}";
            if (_lastErrorFingerprint != fingerprint)
            {
                _lastErrorFingerprint = fingerprint;
                ErrorLogged?.Invoke(error.Message);
            }
        }
    }

    private void SetStatus(ApiHealth status)
    {
        Action<ApiHealth>? handlers;
        lock (_lock)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
            handlers = StatusChanged;
        }

        handlers?.Invoke(status);
    }
}
