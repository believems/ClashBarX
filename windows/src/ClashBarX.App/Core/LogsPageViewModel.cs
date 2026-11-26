using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ClashBarX.App.Core;

/// <summary>日志来源（源：AppLogSource——clashbar 自身与 mihomo 内核）。</summary>
public enum AppLogSource
{
    ClashBar,
    Mihomo,
}

/// <summary>一条应用日志（源：AppErrorLogEntry——最近在前，头部为新）。</summary>
public sealed record AppLogEntry(AppLogSource Source, string Level, string Message, DateTimeOffset Timestamp);

/// <summary>
/// 日志页模型（源：LogsViewModel + AppViewModel+Logging.swift）。
/// mihomo 日志先进缓冲（40 条或 150ms 批量刷，由 DispatcherTimer 驱动 FlushPendingMihomoLogs）；
/// 内存上限随面板可见性切换（可见 5000 / 隐藏 20，保留最新）；渲染上限 120 条，
/// matchedCount 计全部匹配数。
/// </summary>
public sealed class LogsPageViewModel : INotifyPropertyChanged
{
    /// <summary>内存上限与缓冲参数（源：AppViewModel.swift 的 maxInMemoryLogEntries 等）。</summary>
    public const int MaxInMemoryLogEntries = 5000;
    public const int HiddenPanelMaxInMemoryLogEntries = 20;
    public const int MaxBufferedMihomoLogEntries = 40;
    public static readonly TimeSpan MihomoLogFlushInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>渲染上限（源：LogsViewModel.renderLimit = 120）。</summary>
    public const int RenderLimit = 120;

    private readonly List<AppLogEntry> _entries = new();
    private readonly List<AppLogEntry> _pendingMihomoLogs = new();
    private readonly HashSet<string> _selectedLevels = new();
    private bool _isPanelVisible = true;
    private string _searchText = string.Empty;
    private bool _isInfoSelected;
    private bool _isWarningSelected;
    private bool _isErrorSelected;
    private string _matchedCountText = "0";

    public LogsPageViewModel()
    {
        VisibleLogs = new ObservableCollection<LogEntryViewModel>();
        ClearAllCommand = new RelayCommand(_ =>
        {
            ClearAll();
            return Task.CompletedTask;
        });
    }

    public ObservableCollection<LogEntryViewModel> VisibleLogs { get; }

    /// <summary>全部匹配数徽章（渲染上限之外也计数；源：matchedCount）。</summary>
    public string MatchedCountText
    {
        get => _matchedCountText;
        private set => SetProperty(ref _matchedCountText, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RebuildVisibleLogs();
            }
        }
    }

    /// <summary>等级过滤三态 chip（源：selectedLevels——空集 = 全部）。</summary>
    public bool IsInfoSelected
    {
        get => _isInfoSelected;
        set
        {
            if (SetProperty(ref _isInfoSelected, value))
            {
                UpdateLevelFilter("info", value);
            }
        }
    }

    public bool IsWarningSelected
    {
        get => _isWarningSelected;
        set
        {
            if (SetProperty(ref _isWarningSelected, value))
            {
                UpdateLevelFilter("warning", value);
            }
        }
    }

    public bool IsErrorSelected
    {
        get => _isErrorSelected;
        set
        {
            if (SetProperty(ref _isErrorSelected, value))
            {
                UpdateLevelFilter("error", value);
            }
        }
    }

    public ICommand ClearAllCommand { get; }

    /// <summary>App 自身日志：不入缓冲直接入列（源：appendLog(source: .clashbar)）。</summary>
    public void AppendClashBarLog(string message) => AppendClashBarLog("info", message);

    /// <summary>带等级的 App 日志入列（测试与进程 stdout 归一化等级用）。</summary>
    internal void AppendClashBarLog(string level, string message)
    {
        if (message.Length == 0)
        {
            return;
        }

        AppendEntries([new AppLogEntry(AppLogSource.ClashBar, level, message, DateTimeOffset.Now)]);
    }

    /// <summary>
    /// mihomo 日志入缓冲：满 40 条立即刷（源：enqueueBufferedMihomoLog）。
    /// 契约：仅 UI 线程调用（WS 回调须先 Dispatcher 编组），满批直刷会重建可见集合。
    /// </summary>
    public void QueueMihomoLog(string level, string message)
    {
        if (message.Length == 0)
        {
            return;
        }

        lock (_pendingMihomoLogs)
        {
            _pendingMihomoLogs.Add(new AppLogEntry(AppLogSource.Mihomo, level, message, DateTimeOffset.Now));
            if (_pendingMihomoLogs.Count < MaxBufferedMihomoLogEntries)
            {
                return;
            }
        }

        FlushPendingMihomoLogs();
    }

    /// <summary>刷缓冲：批量入列（DispatcherTimer 每 150ms 调用；源：flushPendingMihomoLogsIfNeeded）。</summary>
    public void FlushPendingMihomoLogs()
    {
        List<AppLogEntry> drained;
        lock (_pendingMihomoLogs)
        {
            if (_pendingMihomoLogs.Count == 0)
            {
                return;
            }

            drained = new List<AppLogEntry>(_pendingMihomoLogs);
            _pendingMihomoLogs.Clear();
        }

        AppendEntries(drained);
    }

    /// <summary>面板可见性切换：隐藏时收紧上限并裁剪（源：trimInMemoryLogsForCurrentVisibility）。</summary>
    public void SetPanelVisible(bool visible)
    {
        _isPanelVisible = visible;
        FlushPendingMihomoLogs();
        TrimInMemoryLogs();
    }

    /// <summary>清空全部日志（含缓冲；源：clearAllLogs）。</summary>
    public void ClearAll()
    {
        lock (_pendingMihomoLogs)
        {
            _pendingMihomoLogs.Clear();
        }

        _entries.Clear();
        RebuildVisibleLogs();
    }

    /// <summary>复制全部日志文本（最近在前；源：copyAllLogs）。</summary>
    public string CopyAllText() => string.Join(Environment.NewLine,
        _entries.Select(FormatEntry));

    /// <summary>全部内存条目快照（测试与复制用）。</summary>
    internal IReadOnlyList<AppLogEntry> SnapshotEntries() => _entries.ToList();

    private void AppendEntries(IReadOnlyList<AppLogEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        // 最近在前：批量条目先反转再整体插头部（源：appendLogEntries 的 entries.reversed() + 前插）
        var reversed = new List<AppLogEntry>(entries);
        reversed.Reverse();
        _entries.InsertRange(0, reversed);
        TrimInMemoryLogs();
        RebuildVisibleLogs();
    }

    private void TrimInMemoryLogs()
    {
        int maxEntries = _isPanelVisible ? MaxInMemoryLogEntries : HiddenPanelMaxInMemoryLogEntries;
        if (_entries.Count > maxEntries)
        {
            _entries.RemoveRange(maxEntries, _entries.Count - maxEntries);
        }
    }

    private void RebuildVisibleLogs()
    {
        string keyword = _searchText.Trim();
        int matched = 0;
        var visible = new List<LogEntryViewModel>(Math.Min(RenderLimit, _entries.Count));

        foreach (AppLogEntry entry in _entries)
        {
            if (_selectedLevels.Count > 0 && !_selectedLevels.Contains(entry.Level))
            {
                continue;
            }

            if (keyword.Length > 0 && !entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matched++;
            if (visible.Count < RenderLimit)
            {
                visible.Add(new LogEntryViewModel(
                    entry.Source == AppLogSource.Mihomo ? "Mihomo" : "ClashBarX",
                    entry.Level,
                    entry.Message,
                    entry.Timestamp.ToString("HH:mm:ss")));
            }
        }

        VisibleLogs.Clear();
        foreach (LogEntryViewModel item in visible)
        {
            VisibleLogs.Add(item);
        }

        MatchedCountText = matched.ToString();
    }

    private void UpdateLevelFilter(string level, bool selected)
    {
        if (selected)
        {
            _selectedLevels.Add(level);
        }
        else
        {
            _selectedLevels.Remove(level);
        }

        RebuildVisibleLogs();
    }

    internal static string FormatEntry(AppLogEntry entry) =>
        $"{entry.Timestamp:HH:mm:ss} [{entry.Source}] {entry.Level}: {entry.Message}";

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

/// <summary>日志行：等级配色与图标由 XAML DataTrigger 处理。</summary>
public sealed record LogEntryViewModel(string Source, string Level, string Message, string Timestamp);
