using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ClashBarX.Core.Models;
using ClashBarX.Core.Proxies;

namespace ClashBarX.App.Core;

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
        ICommand? testGroupCommand = null)
    {
        Name = name;
        GroupType = groupType;
        Nodes = new ObservableCollection<ProxyNodeViewModel>(nodes);
        SwitchNodeCommand = switchNodeCommand;
        TestNodeCommand = testNodeCommand;
        TestGroupCommand = testGroupCommand;
    }

    public string Name { get; }

    public string GroupType { get; }

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

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>节点行：名称/类型/延迟/选中态与命令由页面模型注入。</summary>
public sealed class ProxyNodeViewModel : INotifyPropertyChanged
{
    private string _delayText = string.Empty;
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

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
            parts.Add($"总量 ↑{FormatBytes(_upTotal)} ↓{FormatBytes(_downTotal)}");
        }

        if (_connectionsCount is { } connections)
        {
            parts.Add($"连接 {connections}");
        }

        if (_memoryInuse is { } memory)
        {
            parts.Add($"内存 {FormatBytes(memory)}");
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

    /// <summary>刷新分组卡（Core 已按 GLOBAL 序排序）。</summary>
    public void ApplyBoard(ProxyBoard board)
    {
        Groups.Clear();
        foreach (ProxyGroup group in board.Groups)
        {
            var nodes = group.All.Select(nodeName => new ProxyNodeViewModel(
                nodeName,
                board.NodeTypes.GetValueOrDefault(nodeName) ?? string.Empty)
            {
                DelayText = FormatDelay(board.DelaySamples.GetValueOrDefault(nodeName)),
                IsSelected = nodeName == group.Now,
            });
            var groupViewModel = new ProxyGroupViewModel(
                group.Name, group.Type ?? string.Empty, nodes,
                SwitchNodeCommand, TestNodeCommand, TestGroupCommand)
            {
                CurrentNode = group.Now ?? "—",
                DelayText = FormatDelay(group.DelayHistory.Count > 0 ? group.DelayHistory : null),
            };
            Groups.Add(groupViewModel);
        }
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
            }
        }

        group.DelayText = FormatDelay(results.Values.DefaultIfEmpty(0).Max() is var maxDelay && maxDelay > 0
            ? new[] { maxDelay }
            : null);
    }

    /// <summary>单节点测速结果回填。</summary>
    public void ApplyNodeTestResult(string groupName, string nodeName, int? delay)
    {
        ProxyGroupViewModel? group = Groups.FirstOrDefault(item => item.Name == groupName);
        ProxyNodeViewModel? node = group?.Nodes.FirstOrDefault(item => item.Name == nodeName);
        if (node != null)
        {
            node.DelayText = FormatDelay(delay is null ? null : new[] { delay.Value });
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
            _log($"[proxy] 切换 {group.Name} → {node.Name} 失败：{error.Message}");
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
            _log($"[proxy] {group.Name} 测速完成（{results.Count} 节点）");
        }
        catch (Exception error)
        {
            _log($"[proxy] {group.Name} 测速失败：{error.Message}");
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
            _log($"[proxy] {node.Name} 测速失败：{error.Message}");
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
        return last > 0 ? $"{last} ms" : "超时";
    }

    private static string FormatRate(long bytesPerSecond) => bytesPerSecond switch
    {
        < 1024 => $"{bytesPerSecond} B/s",
        < 1024 * 1024 => $"{bytesPerSecond / 1024.0:F1} KB/s",
        _ => $"{bytesPerSecond / (1024.0 * 1024.0):F1} MB/s",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
