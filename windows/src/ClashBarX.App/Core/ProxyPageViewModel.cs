using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ClashBarX.Core.Models;
using ClashBarX.Core.Proxies;

namespace ClashBarX.App.Core;

/// <summary>
/// 延迟历史信号条单根（源：LatencySignalBars——高度由延迟数值决定、颜色表达好坏、
/// 未测槽位为矮桩）。
/// </summary>
public sealed record SignalBarViewModel(double Height, string ColorHex);

/// <summary>代理页的分组视图单元：头（名/当前/延迟）+ 节点行集合。命令由页面模型注入。</summary>
public sealed class ProxyGroupViewModel : INotifyPropertyChanged
{
    private string _currentNode = string.Empty;
    private string _delayText = string.Empty;
    private bool _isTesting;

    public ProxyGroupViewModel(
        string name,
        string groupType,
        IEnumerable<ProxyNodeViewModel> nodes,
        ICommand? switchNodeCommand = null,
        ICommand? testNodeCommand = null,
        ICommand? testGroupCommand = null,
        string? groupIcon = null)
    {
        Name = name;
        GroupType = groupType;
        Nodes = new ObservableCollection<ProxyNodeViewModel>(nodes);
        SwitchNodeCommand = switchNodeCommand;
        TestNodeCommand = testNodeCommand;
        TestGroupCommand = testGroupCommand;
        GroupIcon = groupIcon;
    }

    public string Name { get; }

    public string GroupType { get; }

    /// <summary>分组图标 URL（源：proxyGroupIconURL；null 不显示）。</summary>
    public string? GroupIcon { get; }

    public ObservableCollection<ProxyNodeViewModel> Nodes { get; }

    /// <summary>节点切换命令（由页面模型注入，绑定用）。</summary>
    public ICommand? SwitchNodeCommand { get; }

    /// <summary>单节点测速命令（由页面模型注入）。</summary>
    public ICommand? TestNodeCommand { get; }

    /// <summary>组测速命令（由页面模型注入）。</summary>
    public ICommand? TestGroupCommand { get; }

    public string CurrentNode
    {
        get => _currentNode;
        set => SetProperty(ref _currentNode, value);
    }

    public string DelayText
    {
        get => _delayText;
        set => SetProperty(ref _delayText, value);
    }

    /// <summary>组测速进行中（用于禁用按钮/降透明度）。</summary>
    public bool IsTesting
    {
        get => _isTesting;
        set => SetProperty(ref _isTesting, value);
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

/// <summary>节点行：名称/类型/延迟/信号条/选中态与命令由页面模型注入。</summary>
public sealed class ProxyNodeViewModel : INotifyPropertyChanged
{
    private string _delayText = string.Empty;
    private IReadOnlyList<SignalBarViewModel> _signalBars = Array.Empty<SignalBarViewModel>();
    private bool _isSelected;
    private bool _isTesting;

    public ProxyNodeViewModel(string name, string nodeType)
    {
        Name = name;
        NodeType = nodeType;
    }

    public string Name { get; }

    public string NodeType { get; }

    public string DelayText
    {
        get => _delayText;
        set => SetProperty(ref _delayText, value);
    }

    /// <summary>延迟历史信号条（开启历史视图时 5 槽右对齐；否则仅最新一根）。</summary>
    public IReadOnlyList<SignalBarViewModel> SignalBars
    {
        get => _signalBars;
        set
        {
            if (SetProperty(ref _signalBars, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSignalBars)));
            }
        }
    }

    /// <summary>是否显示信号条区（有样本时）。</summary>
    public bool HasSignalBars => SignalBars.Count > 0;

    /// <summary>是否为分组当前选中节点（高亮）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        set => SetProperty(ref _isTesting, value);
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

/// <summary>简版命令（UI 交互转发到页面模型）。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;

    public RelayCommand(Func<object?, Task> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _ = _execute(parameter);
}

/// <summary>
/// 代理页模型（信息架构源：ProxyTabView/ProxyProvidersAndGroupsView 的分组卡）：
/// 分组集合（GLOBAL 序已由 Core 排序）、流量摘要文本、切换节点/组测速/单节点测速命令。
/// 操作在后台线程执行，UI 状态经 INotifyPropertyChanged 回主线程（构造方负责注入编组上下文）。
/// </summary>
public sealed class ProxyPageViewModel : INotifyPropertyChanged
{
    private readonly ProxyBoardService _board;
    private readonly Action<string> _log;
    private readonly Func<bool> _isCoreRunning;
    private string _trafficSummary = string.Empty;
    private bool _showTrafficOverview = true;
    private string _mode = "rule";
    private ProxyBoard? _lastBoard;

    // ---- 分组卡四视图（源：ProxyGroupsViewModel + compactTopIcon 开关组；会话态不持久化） ----

    private bool _sortNodesByLatency;
    private bool _showLatencyHistory;
    private bool _showHiddenProxyGroups;
    private bool _isTestingAllGroupLatencies;

    /// <summary>用户请求测全部分组（App 层并发测速；源：refreshAllGroupLatencies）。</summary>
    public event Action? TestAllGroupsRequested;

    /// <summary>按延迟排序节点（排序时过滤不可用节点；源：sortGroupNodesByLatency + sortedNodes）。</summary>
    public bool SortNodesByLatency
    {
        get => _sortNodesByLatency;
        set
        {
            if (SetProperty(ref _sortNodesByLatency, value))
            {
                ReapplyBoard();
            }
        }
    }

    /// <summary>延迟历史信号条（源：showLatencyHistory）。</summary>
    public bool ShowLatencyHistory
    {
        get => _showLatencyHistory;
        set
        {
            if (SetProperty(ref _showLatencyHistory, value))
            {
                ReapplyBoard();
            }
        }
    }

    /// <summary>显示 hidden 分组（源：hideHiddenProxyGroups 取反；默认隐藏）。</summary>
    public bool ShowHiddenProxyGroups
    {
        get => _showHiddenProxyGroups;
        set
        {
            if (SetProperty(ref _showHiddenProxyGroups, value))
            {
                ReapplyBoard();
            }
        }
    }

    /// <summary>测全部分组进行中（源：isTestingAllGroupLatencies）。</summary>
    public bool IsTestingAllGroupLatencies
    {
        get => _isTestingAllGroupLatencies;
        set => SetProperty(ref _isTestingAllGroupLatencies, value);
    }

    public ICommand TestAllGroupsCommand { get; }

    public ProxyPageViewModel(
        ProxyBoardService board,
        Action<string> log,
        Func<bool> isCoreRunning)
    {
        _board = board;
        _log = log;
        _isCoreRunning = isCoreRunning;
        Groups = new ObservableCollection<ProxyGroupViewModel>();
        SwitchNodeCommand = new RelayCommand(parameter => ExecuteForNodeAsync(parameter, SwitchNodeCoreAsync));
        TestNodeCommand = new RelayCommand(parameter => ExecuteForNodeAsync(parameter, TestNodeCoreAsync));
        TestGroupCommand = new RelayCommand(parameter => ExecuteForGroupAsync(parameter, TestGroupCoreAsync));
        TestAllGroupsCommand = new RelayCommand(_ =>
        {
            if (!IsTestingAllGroupLatencies)
            {
                TestAllGroupsRequested?.Invoke();
            }

            return Task.CompletedTask;
        });
    }

    public ObservableCollection<ProxyGroupViewModel> Groups { get; }

    public string TrafficSummary
    {
        get => _trafficSummary;
        private set => SetProperty(ref _trafficSummary, value);
    }

    /// <summary>流量概览显隐（源：clashbar.proxy.traffic_overview.visible，默认 true）。</summary>
    public bool ShowTrafficOverview
    {
        get => _showTrafficOverview;
        set => SetProperty(ref _showTrafficOverview, value);
    }

    /// <summary>内核分流模式变化（App 层 PATCH /configs {"mode"}；源：rule/global/direct 切换）。</summary>
    public event Action<string>? ModeChanged;

    /// <summary>分流模式（rule/global/direct；初始化经 ApplyMode 不触发事件）。</summary>
    public string Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mode)));
            ModeChanged?.Invoke(value);
        }
    }

    public bool IsModeRule
    {
        get => _mode == "rule";
        set
        {
            if (value)
            {
                Mode = "rule";
            }
        }
    }

    public bool IsModeGlobal
    {
        get => _mode == "global";
        set
        {
            if (value)
            {
                Mode = "global";
            }
        }
    }

    public bool IsModeDirect
    {
        get => _mode == "direct";
        set
        {
            if (value)
            {
                Mode = "direct";
            }
        }
    }

    /// <summary>初始化分流模式（快照载入；不触发 PATCH）。</summary>
    public void ApplyMode(string mode)
    {
        _mode = string.IsNullOrWhiteSpace(mode) ? "rule" : mode;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsModeRule)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsModeGlobal)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsModeDirect)));
    }

    public ICommand SwitchNodeCommand { get; }

    public ICommand TestNodeCommand { get; }

    public ICommand TestGroupCommand { get; }

    private long _upRate;
    private long _downRate;
    private long? _upTotal;
    private long? _downTotal;
    private int? _connectionsCount;
    private long? _memoryInuse;

    /// <summary>流量摘要（traffic WS 流喂速率与总量；源：Swift 顶部概览——速率+总量+连接数+内存）。</summary>
    public void UpdateTraffic(long upBytesPerSecond, long downBytesPerSecond, long? upTotal = null, long? downTotal = null)
    {
        _upRate = upBytesPerSecond;
        _downRate = downBytesPerSecond;
        _upTotal = upTotal ?? _upTotal;
        _downTotal = downTotal ?? _downTotal;
        RebuildTrafficSummary();
    }

    /// <summary>连接数（connections WS 流的 totalCount）。</summary>
    public void UpdateConnectionsCount(int totalCount)
    {
        _connectionsCount = totalCount;
        RebuildTrafficSummary();
    }

    /// <summary>内存占用（memory WS 流的 inuse）。</summary>
    public void UpdateMemory(long inuseBytes)
    {
        _memoryInuse = inuseBytes;
        RebuildTrafficSummary();
    }

    private void RebuildTrafficSummary()
    {
        var parts = new List<string> { $"↑ {FormatRate(_upRate)}    ↓ {FormatRate(_downRate)}" };
        if (_upTotal is not null || _downTotal is not null)
        {
            parts.Add(ClashBarX.Core.L10n.CoreL10n.T("proxy.traffic.totals", FormatBytes(_upTotal), FormatBytes(_downTotal)));
        }

        if (_connectionsCount is { } connections)
        {
            parts.Add(ClashBarX.Core.L10n.CoreL10n.T("proxy.traffic.connections", connections));
        }

        if (_memoryInuse is { } memory)
        {
            parts.Add(ClashBarX.Core.L10n.CoreL10n.T("proxy.traffic.memory", FormatBytes(memory)));
        }

        TrafficSummary = string.Join(" · ", parts);
    }

    private static string FormatBytes(long? bytes) => bytes switch
    {
        null => "0B",
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1}GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F1}MB",
        >= 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes}B",
    };

    /// <summary>
    /// 刷新分组卡：hidden 分组与 GLOBAL（按模式）过滤（源：filteredProxyGroups）、
    /// 节点排序（按延迟升序、不可用垫尾并过滤；源：sortedNodes）、信号条装配。
    /// </summary>
    public void ApplyBoard(ProxyBoard board)
    {
        _lastBoard = board;
        ReapplyBoard();
    }

    /// <summary>以最近一次面板数据按当前视图开关重排（开关切换即时生效）。</summary>
    private void ReapplyBoard()
    {
        ProxyBoard? board = _lastBoard;
        if (board is null)
        {
            return;
        }

        Groups.Clear();
        foreach (ProxyGroup group in board.Groups.Where(ShouldShowGroup))
        {
            var nodes = OrderGroupNodes(group, board.DelaySamples)
                .Select(nodeName => new ProxyNodeViewModel(
                    nodeName,
                    board.NodeTypes.GetValueOrDefault(nodeName) ?? string.Empty)
                {
                    DelayText = FormatDelay(board.DelaySamples.GetValueOrDefault(nodeName)),
                    SignalBars = ComputeSignalBars(board.DelaySamples.GetValueOrDefault(nodeName)),
                    IsSelected = nodeName == group.Now,
                });
            var groupViewModel = new ProxyGroupViewModel(
                group.Name, group.Type ?? string.Empty, nodes,
                SwitchNodeCommand, TestNodeCommand, TestGroupCommand,
                group.Icon)
            {
                CurrentNode = group.Now ?? "—",
                DelayText = FormatDelay(group.DelayHistory.Count > 0 ? group.DelayHistory : null),
            };
            Groups.Add(groupViewModel);
        }
    }

    /// <summary>分组可见性（源：updateFilteredProxyGroups——非 global 模式滤 GLOBAL；默认滤 hidden）。</summary>
    internal bool ShouldShowGroup(ProxyGroup group) =>
        (Mode == "global" || !string.Equals(group.Name, "GLOBAL", StringComparison.OrdinalIgnoreCase))
        && (ShowHiddenProxyGroups || group.Hidden != true);

    /// <summary>
    /// 节点序（源：sortedGroupNodes/defaultGroupNodes）：默认保持配置序去重；
    /// 排序模式按延迟升序（未测/超时垫尾后按名称），并过滤不可用节点
    /// （延迟为 null 或 0；源：hideUnavailableProxyNodes 在排序路径的过滤）。
    /// </summary>
    internal IEnumerable<string> OrderGroupNodes(
        ProxyGroup group, IReadOnlyDictionary<string, IReadOnlyList<int>> delaySamples)
    {
        var unique = group.All.Where(name => name.Length > 0).Distinct().ToList();
        if (!SortNodesByLatency)
        {
            return unique;
        }

        int? LatencyOf(string name) => delaySamples.GetValueOrDefault(name) is { Count: > 0 } samples
            ? samples[^1]
            : null;

        return unique
            .OrderBy(name => LatencyOf(name) ?? int.MaxValue)   // 未测垫尾（可用按延迟升序）
            .ThenBy(name => name, StringComparer.Ordinal)
            .Where(name => LatencyOf(name) is > 0);
    }

    /// <summary>信号条（源：LatencySignalBars——5 槽右对齐、未测矮桩、高度与颜色按分级）。</summary>
    internal IReadOnlyList<SignalBarViewModel> ComputeSignalBars(IReadOnlyList<int>? samples)
    {
        const int barCount = 5;
        const double placeholderHeight = 2;

        if (ShowLatencyHistory)
        {
            var recent = (samples ?? Array.Empty<int>()).TakeLast(barCount).ToList();
            int padding = Math.Max(0, barCount - recent.Count);
            var bars = new List<SignalBarViewModel>(barCount);
            for (int index = 0; index < padding; index++)
            {
                bars.Add(new SignalBarViewModel(placeholderHeight, "#B8BEC6"));
            }

            bars.AddRange(recent.Select(delay => new SignalBarViewModel(
                LatencyLevelExtensions.Of(delay).SignalBarHeight(),
                LatencyLevelExtensions.Of(delay).ColorHex())));
            return bars;
        }

        // 单根模式：仅显示最新一次（无样本时整区隐藏）
        return samples is { Count: > 0 }
            ? new[]
            {
                new SignalBarViewModel(
                    LatencyLevelExtensions.Of(samples[^1]).SignalBarHeight(),
                    LatencyLevelExtensions.Of(samples[^1]).ColorHex()),
            }
            : Array.Empty<SignalBarViewModel>();
    }

    /// <summary>组测速结果回填节点延迟显示。</summary>
    public void ApplyGroupTestResults(string groupName, IReadOnlyDictionary<string, int> results)
    {
        ProxyGroupViewModel? group = Groups.FirstOrDefault(item => item.Name == groupName);
        if (group == null)
        {
            return;
        }

        foreach (ProxyNodeViewModel node in group.Nodes)
        {
            if (results.TryGetValue(node.Name, out int delay))
            {
                node.DelayText = FormatDelay(new[] { delay });
                node.SignalBars = ComputeSignalBars(new[] { delay });
            }
        }

        group.DelayText = FormatDelay(results.Values.DefaultIfEmpty(0).Max() is var maxDelay && maxDelay > 0
            ? new[] { maxDelay }
            : null);
    }

    /// <summary>单节点测速结果回填（文本与信号条一起更新）。</summary>
    public void ApplyNodeTestResult(string groupName, string nodeName, int? delay)
    {
        ProxyGroupViewModel? group = Groups.FirstOrDefault(item => item.Name == groupName);
        ProxyNodeViewModel? node = group?.Nodes.FirstOrDefault(item => item.Name == nodeName);
        if (node != null)
        {
            node.DelayText = FormatDelay(delay is null ? null : new[] { delay.Value });
            node.SignalBars = ComputeSignalBars(delay is null ? null : new[] { delay.Value });
        }
    }

    private async Task SwitchNodeCoreAsync(ProxyGroupViewModel group, ProxyNodeViewModel node)
    {
        try
        {
            await _board.SwitchNodeAsync(group.Name, node.Name).ConfigureAwait(true);
            group.CurrentNode = node.Name;
            foreach (ProxyNodeViewModel item in group.Nodes)
            {
                item.IsSelected = ReferenceEquals(item, node);
            }
        }
        catch (Exception error)
        {
            _log(ClashBarX.Core.L10n.CoreL10n.T("log.proxy.switch_failed", group.Name, node.Name, error.Message));
        }
    }

    private async Task TestGroupCoreAsync(ProxyGroupViewModel group)
    {
        if (group.IsTesting)
        {
            return;
        }

        group.IsTesting = true;
        try
        {
            IReadOnlyDictionary<string, int> results = await _board
                .TestGroupAsync(group.Name).ConfigureAwait(true);
            ApplyGroupTestResults(group.Name, results);
            _log(ClashBarX.Core.L10n.CoreL10n.T("log.proxy.group_test_done", group.Name, results.Count));
        }
        catch (Exception error)
        {
            _log(ClashBarX.Core.L10n.CoreL10n.T("log.proxy.group_test_failed", group.Name, error.Message));
        }
        finally
        {
            group.IsTesting = false;
        }
    }

    private async Task TestNodeCoreAsync(ProxyGroupViewModel group, ProxyNodeViewModel node)
    {
        if (node.IsTesting)
        {
            return;
        }

        node.IsTesting = true;
        try
        {
            int? delay = await _board.TestNodeAsync(group.Name, node.Name).ConfigureAwait(true);
            ApplyNodeTestResult(group.Name, node.Name, delay);
        }
        catch (Exception error)
        {
            _log(ClashBarX.Core.L10n.CoreL10n.T("log.proxy.node_test_failed", node.Name, error.Message));
        }
        finally
        {
            node.IsTesting = false;
        }
    }

    private async Task ExecuteForNodeAsync(object? parameter,
        Func<ProxyGroupViewModel, ProxyNodeViewModel, Task> operation)
    {
        if (parameter is ProxyNodeViewModel node && _isCoreRunning())
        {
            ProxyGroupViewModel? group = Groups.FirstOrDefault(item => item.Nodes.Contains(node));
            if (group != null)
            {
                await operation(group, node);
            }
        }
    }

    private async Task ExecuteForGroupAsync(object? parameter, Func<ProxyGroupViewModel, Task> operation)
    {
        if (parameter is ProxyGroupViewModel group && _isCoreRunning())
        {
            await operation(group);
        }
    }

    internal static string FormatDelay(IReadOnlyList<int>? samples)
    {
        if (samples is not { Count: > 0 })
        {
            return "—";
        }

        int last = samples[^1];
        return last > 0 ? $"{last} ms" : ClashBarX.Core.L10n.CoreL10n.T("proxy.delay.timeout");
    }

    private static string FormatRate(long bytesPerSecond) => bytesPerSecond switch
    {
        < 1024 => $"{bytesPerSecond} B/s",
        < 1024 * 1024 => $"{bytesPerSecond / 1024.0:F1} KB/s",
        _ => $"{bytesPerSecond / (1024.0 * 1024.0):F1} MB/s",
    };

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
