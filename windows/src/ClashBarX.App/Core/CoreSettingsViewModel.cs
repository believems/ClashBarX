using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClashBarX.App.Core;

/// <summary>
/// 核心设置区模型（源：SystemTabView 核心设置 + 代理端口区）：
/// allow-lan / ipv6 / tcp-concurrent / 日志等级经 PATCH /configs 即时生效；
/// 代理端口五项 750ms 防抖自动保存（合法 0-65535 整数才提交）；切节点关连接为本地偏好。
/// PATCH 委托与防抖工厂均注入（可测）。
/// </summary>
public sealed class CoreSettingsViewModel : INotifyPropertyChanged
{
    /// <summary>端口防抖间隔（源：代理端口 onChange 750ms 防抖）。</summary>
    public static readonly TimeSpan PortDebounceInterval = TimeSpan.FromMilliseconds(750);

    private bool _allowLan;
    private bool _ipv6;
    private bool _tcpConcurrent;
    private bool _closeConnections;
    private string _logLevel = "info";
    private string _mixedPortText = string.Empty;
    private string _portText = string.Empty;
    private string _socksPortText = string.Empty;
    private string _redirPortText = string.Empty;
    private string _tproxyPortText = string.Empty;
    private bool _isApplyingPatch;

    /// <summary>PATCH 委托：App 层转为 PATCH /configs（key 为 mihomo 原始 JSON 键）。</summary>
    public Func<string, object?, Task>? PatchConfigAsync { get; set; }

    /// <summary>防抖工厂：默认 DispatcherTimer 750ms；测试注入立即执行。</summary>
    public Func<Action, IDisposable>? DebounceFactory { get; set; }

    private IDisposable? _portDebounce;
    private (string Key, string Value)? _pendingPortPatch;

    /// <summary>切节点时关闭旧连接（本地偏好，源：clashbar.proxy.switch.close_connections）。</summary>
    public event Action<bool>? CloseConnectionsChanged;

    public bool AllowLan
    {
        get => _allowLan;
        set
        {
            if (SetProperty(ref _allowLan, value))
            {
                _ = PatchAsync("allow-lan", value);
            }
        }
    }

    public bool Ipv6
    {
        get => _ipv6;
        set
        {
            if (SetProperty(ref _ipv6, value))
            {
                _ = PatchAsync("ipv6", value);
            }
        }
    }

    public bool TcpConcurrent
    {
        get => _tcpConcurrent;
        set
        {
            if (SetProperty(ref _tcpConcurrent, value))
            {
                _ = PatchAsync("tcp-concurrent", value);
            }
        }
    }

    public bool CloseConnections
    {
        get => _closeConnections;
        set
        {
            if (SetProperty(ref _closeConnections, value))
            {
                CloseConnectionsChanged?.Invoke(value);
            }
        }
    }

    /// <summary>日志等级变化（日志页快捷入口双向同步用）。</summary>
    public event Action<string>? LogLevelChanged;

    /// <summary>日志等级（debug/info/warning/error/silent；源：log-level）。</summary>
    public string LogLevel
    {
        get => _logLevel;
        set
        {
            if (SetProperty(ref _logLevel, value) && value.Length > 0)
            {
                LogLevelChanged?.Invoke(value);
                _ = PatchAsync("log-level", value);
            }
        }
    }

    /// <summary>同步等级显示（日志页或快照回推；不触发 PATCH 与事件）。</summary>
    public void SyncLogLevel(string level)
    {
        _logLevel = string.IsNullOrWhiteSpace(level) ? "info" : level;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LogLevel)));
    }

    public IReadOnlyList<PolicyOption> LogLevelOptions { get; } = new[]
    {
        new PolicyOption("debug", "debug"),
        new PolicyOption("info", "info"),
        new PolicyOption("warning", "warning"),
        new PolicyOption("error", "error"),
        new PolicyOption("silent", "silent"),
    };

    public bool IsApplyingPatch
    {
        get => _isApplyingPatch;
        private set => SetProperty(ref _isApplyingPatch, value);
    }

    // ---- 代理端口五项（750ms 防抖保存） ----

    public string MixedPortText
    {
        get => _mixedPortText;
        set
        {
            if (SetProperty(ref _mixedPortText, value))
            {
                SchedulePortPatch("mixed-port", value);
            }
        }
    }

    public string PortText
    {
        get => _portText;
        set
        {
            if (SetProperty(ref _portText, value))
            {
                SchedulePortPatch("port", value);
            }
        }
    }

    public string SocksPortText
    {
        get => _socksPortText;
        set
        {
            if (SetProperty(ref _socksPortText, value))
            {
                SchedulePortPatch("socks-port", value);
            }
        }
    }

    public string RedirPortText
    {
        get => _redirPortText;
        set
        {
            if (SetProperty(ref _redirPortText, value))
            {
                SchedulePortPatch("redir-port", value);
            }
        }
    }

    public string TproxyPortText
    {
        get => _tproxyPortText;
        set
        {
            if (SetProperty(ref _tproxyPortText, value))
            {
                SchedulePortPatch("tproxy-port", value);
            }
        }
    }

    /// <summary>初始化：从运行配置快照载入（不触发 PATCH；调用方负责 UI 线程）。</summary>
    public void ApplySnapshot(bool? allowLan, bool? ipv6, bool? tcpConcurrent, string? logLevel,
        int? mixedPort, int? port, int? socksPort, int? redirPort, int? tproxyPort)
    {
        _allowLan = allowLan ?? false;
        _ipv6 = ipv6 ?? false;
        _tcpConcurrent = tcpConcurrent ?? false;
        _logLevel = string.IsNullOrWhiteSpace(logLevel) ? "info" : logLevel;
        _mixedPortText = mixedPort?.ToString() ?? string.Empty;
        _portText = port?.ToString() ?? string.Empty;
        _socksPortText = socksPort?.ToString() ?? string.Empty;
        _redirPortText = redirPort?.ToString() ?? string.Empty;
        _tproxyPortText = tproxyPort?.ToString() ?? string.Empty;
        _pendingPortPatch = null;   // 初始化值不算待保存草稿
        foreach (string property in new[]
                 {
                     nameof(AllowLan), nameof(Ipv6), nameof(TcpConcurrent), nameof(LogLevel),
                     nameof(MixedPortText), nameof(PortText), nameof(SocksPortText),
                     nameof(RedirPortText), nameof(TproxyPortText),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    /// <summary>恢复本地偏好（不触发事件）。</summary>
    public void RestoreCloseConnections(bool closeConnections)
    {
        _closeConnections = closeConnections;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CloseConnections)));
    }

    private async Task PatchAsync(string key, object? value)
    {
        if (PatchConfigAsync is null)
        {
            return;
        }

        IsApplyingPatch = true;
        try
        {
            await PatchConfigAsync(key, value);
        }
        finally
        {
            IsApplyingPatch = false;
        }
    }

    /// <summary>端口防抖：连续输入只提交最后一次（源：onChange 750ms 防抖）。</summary>
    private void SchedulePortPatch(string key, string value)
    {
        _pendingPortPatch = (key, value);
        _portDebounce?.Dispose();
        Func<Action, IDisposable> factory = DebounceFactory ?? DefaultDebounce;
        _portDebounce = factory(() =>
        {
            if (_pendingPortPatch is { } pending)
            {
                _pendingPortPatch = null;
                _ = PatchPortAsync(pending.Key, pending.Value);
            }
        });
    }

    private async Task PatchPortAsync(string key, string value)
    {
        // 合法 0-65535 整数才提交（0 = 关闭该端口；非法输入静默忽略）
        if (int.TryParse(value.Trim(), out int port) && port is >= 0 and <= 65535)
        {
            await PatchAsync(key, port);
        }
    }

    private static IDisposable DefaultDebounce(Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = PortDebounceInterval,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new DelegateDisposable(timer.Stop);
    }

    private sealed record DelegateDisposable(Action DisposeAction) : IDisposable
    {
        public void Dispose() => DisposeAction();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
