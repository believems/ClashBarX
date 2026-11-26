using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ClashBarX.Core.Models;

namespace ClashBarX.App.Core;

/// <summary>传输协议过滤（源：ConnectionsViewModel.ConnectionsTransportFilter）。</summary>
public enum ConnectionsTransportFilter
{
    All,
    Tcp,
    Udp,
    Other,
}

/// <summary>排序键（源：ConnectionsSortKey。Default 保持内核序，不支持方向）。</summary>
public enum ConnectionsSortKey
{
    Default,
    StartTime,
    DownloadSpeed,
    UploadSpeed,
    Download,
    Upload,
    Total,
}

/// <summary>枚举下拉选项（Label 供显示、Value 供 SelectedValue 绑定）。</summary>
public sealed record EnumOption<TEnum>(string Label, TEnum Value) where TEnum : struct, Enum;

/// <summary>
/// 连接页模型（源：ConnectionsViewModel + ConnectionsTabView 的行语义）。
/// 速率由帧间差值推导（mihomo 只报累计字节）：新值权重 0.5、间隔下限 0.2s，
/// elapsed 取真实流逝时间。悬停列表期间暂停重排，行不跳位。
/// </summary>
public sealed class ConnectionsPageViewModel : INotifyPropertyChanged
{
    /// <summary>速率平滑权重与最低采样间隔（源：speedSmoothing / minimumSampleInterval）。</summary>
    private const double SpeedSmoothing = 0.5;
    private const double MinimumSampleInterval = 0.2;
    private const int RetainedRowLimit = 120;

    private readonly Func<double> _now;
    private readonly Func<string, Task> _closeConnection;
    private readonly Func<Task> _closeAllConnections;
    private readonly Action<string> _log;

    private string _filterText = string.Empty;
    private ConnectionsTransportFilter _transportFilter = ConnectionsTransportFilter.All;
    private ConnectionsSortKey _sortKey = ConnectionsSortKey.Default;
    private bool _isSortDescending = true;
    private bool _isReorderingPaused;
    private string _summaryText = "0/0";
    private bool _showsStatistics;
    private TrafficStatisticsDimension _statisticsDimension = TrafficStatisticsDimension.Node;
    private TrafficStatisticsSnapshot? _statistics;

    private Dictionary<string, TrafficSample> _samples = new();
    private Dictionary<string, (double Upload, double Download)> _speeds = new();
    private IReadOnlyList<ConnectionSummary> _connections = Array.Empty<ConnectionSummary>();

    private readonly struct TrafficSample(long upload, long download, double time, double uploadSpeed, double downloadSpeed)
    {
        public long Upload { get; } = upload;
        public long Download { get; } = download;
        public double Time { get; } = time;
        public double UploadSpeed { get; } = uploadSpeed;
        public double DownloadSpeed { get; } = downloadSpeed;
    }

    public ConnectionsPageViewModel(
        Func<string, Task> closeConnection,
        Func<Task> closeAllConnections,
        Action<string> log,
        Func<double>? now = null)
    {
        _closeConnection = closeConnection;
        _closeAllConnections = closeAllConnections;
        _log = log;
        _now = now ?? DefaultNow;
        CloseConnectionCommand = new RelayCommand(parameter =>
        {
            if (parameter is ConnectionRowViewModel row)
            {
                return _closeConnection(row.Id);
            }

            return Task.CompletedTask;
        });
        CloseAllConnectionsCommand = new RelayCommand(_ => CloseAllCoreAsync());
    }

    public ObservableCollection<ConnectionRowViewModel> Connections { get; } = new();

    /// <summary>传输过滤下拉选项（中文标签，源：ui.network.filter.transport.*）。</summary>
    public IReadOnlyList<EnumOption<ConnectionsTransportFilter>> TransportFilterOptions { get; } = new[]
    {
        new EnumOption<ConnectionsTransportFilter>("全部", ConnectionsTransportFilter.All),
        new EnumOption<ConnectionsTransportFilter>("TCP", ConnectionsTransportFilter.Tcp),
        new EnumOption<ConnectionsTransportFilter>("UDP", ConnectionsTransportFilter.Udp),
        new EnumOption<ConnectionsTransportFilter>("其他", ConnectionsTransportFilter.Other),
    };

    /// <summary>排序键下拉选项（源：ui.network.sort.key.*）。</summary>
    public IReadOnlyList<EnumOption<ConnectionsSortKey>> SortKeyOptions { get; } = new[]
    {
        new EnumOption<ConnectionsSortKey>("默认顺序", ConnectionsSortKey.Default),
        new EnumOption<ConnectionsSortKey>("连接时间", ConnectionsSortKey.StartTime),
        new EnumOption<ConnectionsSortKey>("下载速度", ConnectionsSortKey.DownloadSpeed),
        new EnumOption<ConnectionsSortKey>("上传速度", ConnectionsSortKey.UploadSpeed),
        new EnumOption<ConnectionsSortKey>("下载流量", ConnectionsSortKey.Download),
        new EnumOption<ConnectionsSortKey>("上传流量", ConnectionsSortKey.Upload),
        new EnumOption<ConnectionsSortKey>("总流量", ConnectionsSortKey.Total),
    };

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                RebuildRows();
            }
        }
    }

    public ConnectionsTransportFilter TransportFilter
    {
        get => _transportFilter;
        set
        {
            if (SetProperty(ref _transportFilter, value))
            {
                RebuildRows();
            }
        }
    }

    public ConnectionsSortKey SortKey
    {
        get => _sortKey;
        set
        {
            if (SetProperty(ref _sortKey, value))
            {
                RebuildRows();
            }
        }
    }

    /// <summary>排序方向（源：ConnectionsSortDirection，ToggleButton IsChecked 绑定）。</summary>
    public bool IsSortDescending
    {
        get => _isSortDescending;
        set
        {
            if (SetProperty(ref _isSortDescending, value))
            {
                RebuildRows();
            }
        }
    }

    /// <summary>列表悬停中：暂停重排，已显示行保持位置（源：isReorderingPaused）。</summary>
    public bool IsReorderingPaused
    {
        get => _isReorderingPaused;
        set => SetProperty(ref _isReorderingPaused, value);
    }

    /// <summary>「可见 / 保留上限」计数徽章（源：fractionSummaryBadge）。</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>统计视图切换（源：connectionsStore.showsStatistics——segmented「连接/统计」）。</summary>
    public bool ShowsStatistics
    {
        get => _showsStatistics;
        set
        {
            if (SetProperty(ref _showsStatistics, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsList)));
            }
        }
    }

    public bool ShowsList => !ShowsStatistics;

    /// <summary>统计维度（源：connectionsStore.statisticsDimension，默认 node）。</summary>
    public TrafficStatisticsDimension StatisticsDimension
    {
        get => _statisticsDimension;
        set
        {
            if (SetProperty(ref _statisticsDimension, value))
            {
                RebuildStatisticsRows();
            }
        }
    }

    /// <summary>维度下拉选项（源：ui.statistics.dimension.*）。</summary>
    public IReadOnlyList<EnumOption<TrafficStatisticsDimension>> DimensionOptions { get; } = new[]
    {
        new EnumOption<TrafficStatisticsDimension>("节点", TrafficStatisticsDimension.Node),
        new EnumOption<TrafficStatisticsDimension>("应用", TrafficStatisticsDimension.Application),
        new EnumOption<TrafficStatisticsDimension>("域名", TrafficStatisticsDimension.Domain),
    };

    /// <summary>统计条目（源：TrafficStatisticsView 的行集合）。</summary>
    public ObservableCollection<StatisticRowViewModel> StatisticsRows { get; } = new();

    /// <summary>采样提示（源：ui.statistics.session + sampling_hint）。</summary>
    public string StatisticsHint => "本次运行累计（重启或切换内核后清零，采样统计可能漏计短连接）";

    public ICommand CloseConnectionCommand { get; }

    public ICommand CloseAllConnectionsCommand { get; }

    /// <summary>喂入一帧连接快照：先做速率差分，再按过滤/排序重建行；带统计时刷新统计视图。</summary>
    public void IngestSnapshot(ConnectionStatisticsUpdate update)
    {
        if (update.Connections is { } connections)
        {
            _connections = connections;
            IngestSpeeds(connections);
        }

        // 仅维度一致的快照才显示（源：statistics.dimension == statisticsDimension 才入 store）
        if (update.Statistics is { } statistics && statistics.Dimension == StatisticsDimension)
        {
            _statistics = statistics;
            RebuildStatisticsRows();
        }

        RebuildRows();
        SummaryText = $"{Connections.Count}/{Math.Min(update.TotalCount, RetainedRowLimit)}";
    }

    /// <summary>重建统计行（源：TrafficStatisticsView——名称/合计/上下行，空名显示「未识别」）。</summary>
    private void RebuildStatisticsRows()
    {
        if (!_showsStatistics)
        {
            return;
        }

        StatisticsRows.Clear();
        if (_statistics is null)
        {
            return;
        }

        foreach (TrafficStatisticsEntry entry in _statistics.Entries)
        {
            StatisticsRows.Add(new StatisticRowViewModel(
                string.IsNullOrWhiteSpace(entry.Name) ? "未识别" : entry.Name,
                FormatBytes(entry.Total),
                FormatBytes(entry.Upload),
                FormatBytes(entry.Download)));
        }
    }

    /// <summary>差分速率（源：ingestSnapshot——新值权重 0.5，间隔不足沿用旧样本）。</summary>
    private void IngestSpeeds(IReadOnlyList<ConnectionSummary> connections)
    {
        double now = _now();
        var nextSamples = new Dictionary<string, TrafficSample>(capacity: Math.Max(connections.Count, 1));
        var nextSpeeds = new Dictionary<string, (double, double)>(capacity: Math.Max(connections.Count, 1));

        foreach (ConnectionSummary connection in connections)
        {
            long upload = connection.Upload ?? 0;
            long download = connection.Download ?? 0;
            double uploadSpeed = 0;
            double downloadSpeed = 0;

            if (_samples.TryGetValue(connection.Id, out TrafficSample previous))
            {
                double elapsed = now - previous.Time;
                if (elapsed < MinimumSampleInterval)
                {
                    nextSamples[connection.Id] = previous;
                    nextSpeeds[connection.Id] = (previous.UploadSpeed, previous.DownloadSpeed);
                    continue;
                }

                double weight = SpeedSmoothing;
                uploadSpeed = weight * Math.Max(0, upload - previous.Upload) / elapsed
                    + (1 - weight) * previous.UploadSpeed;
                downloadSpeed = weight * Math.Max(0, download - previous.Download) / elapsed
                    + (1 - weight) * previous.DownloadSpeed;
            }

            nextSamples[connection.Id] = new TrafficSample(upload, download, now, uploadSpeed, downloadSpeed);
            nextSpeeds[connection.Id] = (uploadSpeed, downloadSpeed);
        }

        _samples = nextSamples;
        _speeds = nextSpeeds;
    }

    /// <summary>过滤 + 排序 + 悬停保序后重建行集合（源：updateVisibleConnections）。</summary>
    private void RebuildRows()
    {
        IEnumerable<ConnectionSummary> source = _connections.Take(RetainedRowLimit);
        string keyword = _filterText.Trim();

        if (keyword.Length > 0 || _transportFilter != ConnectionsTransportFilter.All)
        {
            source = source.Where(connection => MatchesFilter(connection, keyword));
        }

        List<ConnectionSummary> sorted = SortConnections(source).ToList();
        if (IsReorderingPaused)
        {
            sorted = PreserveCurrentOrder(sorted);
        }

        Connections.Clear();
        foreach (ConnectionSummary connection in sorted)
        {
            Connections.Add(CreateRow(connection));
        }
    }

    private bool MatchesFilter(ConnectionSummary connection, string keyword)
    {
        if (!MatchesTransport(connection.Metadata?.Network))
        {
            return false;
        }

        if (keyword.Length == 0)
        {
            return true;
        }

        return SearchText(connection).Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesTransport(string? network)
    {
        string normalized = (network ?? string.Empty).Trim().ToLowerInvariant();
        return _transportFilter switch
        {
            ConnectionsTransportFilter.All => true,
            ConnectionsTransportFilter.Tcp => normalized == "tcp",
            ConnectionsTransportFilter.Udp => normalized == "udp",
            ConnectionsTransportFilter.Other => normalized.Length > 0 && normalized != "tcp" && normalized != "udp",
            _ => true,
        };
    }

    /// <summary>搜索匹配串（源：connectionsSearchText——host/IP/网络/ID/规则/链路/时间）。</summary>
    internal static string SearchText(ConnectionSummary connection)
    {
        ConnectionMetadata? metadata = connection.Metadata;
        return string.Join(' ',
            metadata?.Host ?? string.Empty,
            metadata?.DestinationIP ?? string.Empty,
            metadata?.SourceIP ?? string.Empty,
            metadata?.Network ?? string.Empty,
            connection.Id,
            connection.Rule ?? string.Empty,
            connection.RulePayload ?? string.Empty,
            connection.Chains is { } chains ? string.Join(' ', chains) : string.Empty,
            connection.Start ?? string.Empty);
    }

    private List<ConnectionSummary> SortConnections(IEnumerable<ConnectionSummary> source)
    {
        if (_sortKey == ConnectionsSortKey.Default)
        {
            return source.ToList();
        }

        bool descending = _isSortDescending;
        return source
            .Select(connection => (connection, Metric: SortMetric(connection)))
            .OrderBy(pair => pair.Metric, descending
                ? Comparer<double>.Create((left, right) => right.CompareTo(left))
                : Comparer<double>.Default)
            .ThenBy(pair => pair.connection.Id, StringComparer.Ordinal)
            .Select(pair => pair.connection)
            .ToList();
    }

    private double SortMetric(ConnectionSummary connection) => _sortKey switch
    {
        ConnectionsSortKey.StartTime => connection.StartTimestamp ?? 0,
        ConnectionsSortKey.DownloadSpeed => _speeds.GetValueOrDefault(connection.Id).Download,
        ConnectionsSortKey.UploadSpeed => _speeds.GetValueOrDefault(connection.Id).Upload,
        ConnectionsSortKey.Download => connection.Download ?? 0,
        ConnectionsSortKey.Upload => connection.Upload ?? 0,
        ConnectionsSortKey.Total => (connection.Upload ?? 0) + (connection.Download ?? 0),
        _ => 0,
    };

    /// <summary>已显示行保持原位、新行追加尾部（源：preservingCurrentOrder）。</summary>
    private List<ConnectionSummary> PreserveCurrentOrder(List<ConnectionSummary> sorted)
    {
        var currentIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < Connections.Count; index++)
        {
            currentIndex.TryAdd(Connections[index].Id, index);
        }

        List<ConnectionSummary> kept = sorted
            .Where(connection => currentIndex.ContainsKey(connection.Id))
            .OrderBy(connection => currentIndex[connection.Id])
            .ToList();
        IEnumerable<ConnectionSummary> added = sorted.Where(connection => !currentIndex.ContainsKey(connection.Id));
        return [.. kept, .. added];
    }

    private ConnectionRowViewModel CreateRow(ConnectionSummary connection)
    {
        (double uploadSpeed, double downloadSpeed) = _speeds.GetValueOrDefault(connection.Id);
        return new ConnectionRowViewModel(
            connection.Id,
            HostText(connection),
            RuleText(connection),
            (connection.Metadata?.Network ?? string.Empty).ToUpperInvariant(),
            ChainText(connection),
            StartedText(connection),
            FormatBytes(connection.Upload ?? 0),
            FormatBytes(connection.Download ?? 0),
            FormatRate(uploadSpeed),
            FormatRate(downloadSpeed))
        {
            CloseConnectionCommand = CloseConnectionCommand,
        };
    }

    internal static string HostText(ConnectionSummary connection)
    {
        string? host = connection.Metadata?.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            host = connection.Metadata?.DestinationIP;
        }

        return string.IsNullOrWhiteSpace(host) ? "-" : host;
    }

    /// <summary>规则文本：MATCH/FINAL 显示 "--"（不区分大小写，mihomo 实测上报 "Match"），payload 优先。</summary>
    internal static string RuleText(ConnectionSummary connection)
    {
        string? rule = connection.Rule?.Trim();
        if (string.IsNullOrEmpty(rule)
            || rule.Equals("MATCH", StringComparison.OrdinalIgnoreCase)
            || rule.Equals("FINAL", StringComparison.OrdinalIgnoreCase))
        {
            return "--";
        }

        string? payload = connection.RulePayload?.Trim();
        return string.IsNullOrEmpty(payload) ? rule : $"{rule}({payload})";
    }

    /// <summary>链路文本：chains 反转（叶端在前）以 " &gt; " 连接（源：ChainText）。</summary>
    internal static string ChainText(ConnectionSummary connection) =>
        connection.Chains is { Count: > 0 } chains
            ? string.Join(" › ", chains.Reverse())
            : "-";

    private static string StartedText(ConnectionSummary connection) =>
        connection.StartTimestamp is { } timestamp
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(timestamp * 1000)).LocalDateTime.ToString("HH:mm:ss")
            : "-";

    /// <summary>1024 进制紧凑字节（源：ValueFormatter.bytesCompactNoSpace）。</summary>
    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024 * 1024):F1}TB",
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1}GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F1}MB",
        >= 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes}B",
    };

    /// <summary>速率文本：小于 1 B/s 视为空闲显示 "–"（源：rateText）。</summary>
    internal static string FormatRate(double bytesPerSecond) =>
        bytesPerSecond < 1 ? "–" : FormatBytes((long)bytesPerSecond) + "/s";

    private async Task CloseAllCoreAsync()
    {
        try
        {
            await _closeAllConnections();
        }
        catch (Exception error)
        {
            _log($"[connections] 关闭全部失败：{error.Message}");
        }
    }

    private static double DefaultNow() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

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

/// <summary>连接行：文本由页面模型格式化，关闭命令注入。</summary>
public sealed class ConnectionRowViewModel : INotifyPropertyChanged
{
    public ConnectionRowViewModel(
        string id,
        string host,
        string rule,
        string network,
        string chain,
        string started,
        string upload,
        string download,
        string uploadRate,
        string downloadRate)
    {
        Id = id;
        Host = host;
        Rule = rule;
        Network = network;
        Chain = chain;
        Started = started;
        Upload = upload;
        Download = download;
        UploadRate = uploadRate;
        DownloadRate = downloadRate;
    }

    public string Id { get; }

    public string Host { get; }

    public string Rule { get; }

    public string Network { get; }

    public string Chain { get; }

    public string Started { get; }

    public string Upload { get; }

    public string Download { get; }

    public string UploadRate { get; }

    public string DownloadRate { get; }

    public ICommand? CloseConnectionCommand { get; internal set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>统计行：名称 + 合计 + 上/下行字节（源：TrafficStatisticsView 的行）。</summary>
public sealed record StatisticRowViewModel(string Name, string TotalText, string UploadText, string DownloadText);
