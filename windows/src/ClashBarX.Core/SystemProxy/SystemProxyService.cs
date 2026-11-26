namespace ClashBarX.Core.SystemProxy;

/// <summary>
/// 系统代理编排（源：AppViewModel+SystemProxy.swift 的意图管理与守护语义）：
/// 显式开/关记录期望目标；外部改动（RegistryWatcher）经 0.3s 去抖后核对，
/// 仅在存在开启意图且系统值偏离期望时回写修复（startup_repaired 语义）；
/// 无意图时不越权回写、只同步观测状态。自身写注册表引发的变更通知会被抑制一轮。
/// 回调可能来自线程池线程，UI 层需自行编组。
/// </summary>
public sealed class SystemProxyService : IDisposable
{
    private readonly ISystemProxyController _controller;
    private readonly TimeSpan _debounceInterval;
    private readonly object _lock = new();
    private Timer? _debounceTimer;
    private SystemProxyTarget? _expected;
    private SystemProxySnapshot? _lastSnapshot;
    private bool _suppressNextNotification;

    /// <summary>状态与修复动作日志（单行文本）。</summary>
    public event Action<string>? Logged;

    /// <summary>观测到的系统代理开关状态变化（含外部改动与显式开关）。</summary>
    public event Action<bool>? EnabledChanged;

    public SystemProxyService(
        ISystemProxyController controller,
        TimeSpan? debounceInterval = null)
    {
        _controller = controller;
        _debounceInterval = debounceInterval ?? TimeSpan.FromMilliseconds(300);
    }

    /// <summary>期望目标（非 null 即存在开启意图）。</summary>
    public SystemProxyTarget? ExpectedTarget
    {
        get { lock (_lock) return _expected; }
    }

    /// <summary>最近一次观测快照。</summary>
    public SystemProxySnapshot? LastSnapshot
    {
        get { lock (_lock) return _lastSnapshot; }
    }

    /// <summary>显式启用系统代理并记录期望目标。</summary>
    public void Enable(SystemProxyTarget target)
    {
        _controller.Enable(target);
        lock (_lock)
        {
            _expected = target;
            _lastSnapshot = _controller.Read();
            _suppressNextNotification = true;   // 自写引发的注册表变更不再触发守护
        }

        Logged?.Invoke($"[system proxy] enabled {target.ServerDisplay}");
        EnabledChanged?.Invoke(true);
    }

    /// <summary>显式禁用系统代理并清除开启意图。</summary>
    public void Disable()
    {
        _controller.Disable();
        lock (_lock)
        {
            _expected = null;
            _lastSnapshot = _controller.Read();
            _suppressNextNotification = true;
        }

        Logged?.Invoke("[system proxy] disabled");
        EnabledChanged?.Invoke(false);
    }

    /// <summary>
    /// 注册表变更通知入口（RegistryWatcher 事件）。去抖后核对：有意图且偏离 → 回写修复；
    /// 无意图 → 仅同步观测状态。
    /// </summary>
    public void NotifyRegistryChanged()
    {
        lock (_lock)
        {
            if (_suppressNextNotification)
            {
                _suppressNextNotification = false;
                return;
            }

            _debounceTimer ??= new Timer(
                _ => VerifyAgainstExpected(), null, _debounceInterval, Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// 主动核对当前系统值（源：startSystemProxyMonitoring 的 3 秒周期核对路径），
    /// 返回最新快照并按需修复。
    /// </summary>
    public SystemProxySnapshot RefreshState()
    {
        SystemProxySnapshot snapshot = _controller.Read();
        bool repaired = false;
        lock (_lock)
        {
            if (SystemProxyGuard.NeedsRewrite(snapshot, _expected))
            {
                _controller.Enable(_expected!);
                _suppressNextNotification = true;
                snapshot = _controller.Read();
                repaired = true;
            }

            _lastSnapshot = snapshot;
        }

        if (repaired)
        {
            Logged?.Invoke($"[system proxy] repaired to {_expected!.ServerDisplay}");
        }

        EnabledChanged?.Invoke(snapshot.Enabled);
        return snapshot;
    }

    private void VerifyAgainstExpected()
    {
        try
        {
            RefreshState();
        }
        catch (Exception error)
        {
            Logged?.Invoke($"[system proxy] guard failed: {error.Message}");
        }
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_lock)
        {
            timer = _debounceTimer;
            _debounceTimer = null;
        }

        timer?.Dispose();
    }
}
