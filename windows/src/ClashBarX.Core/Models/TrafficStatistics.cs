namespace ClashBarX.Core.Models;

/// <summary>流量统计维度（源：TrafficStatistics.swift 的 TrafficStatisticsDimension）。</summary>
public enum TrafficStatisticsDimension
{
    Node,
    Application,
    Domain,
}

/// <summary>按维度聚合的一条流量统计（源：TrafficStatisticsEntry）。</summary>
public sealed record TrafficStatisticsEntry(
    string Id,
    string Name,
    long Upload,
    long Download)
{
    public long Total => Upload + Download;
}

/// <summary>
/// 单维度统计快照（源：TrafficStatisticsSnapshot）。条目按 total 降序、
/// 再 name、id 升序稳定排序；空快照维度固定为 node。
/// </summary>
public sealed record TrafficStatisticsSnapshot(
    TrafficStatisticsDimension Dimension,
    IReadOnlyList<TrafficStatisticsEntry> Entries)
{
    public static readonly TrafficStatisticsSnapshot Empty = new(
        TrafficStatisticsDimension.Node, Array.Empty<TrafficStatisticsEntry>());
}

/// <summary>一帧连接流的处理结果（源：ConnectionStatisticsUpdate）。</summary>
public sealed record ConnectionStatisticsUpdate(
    int TotalCount,
    long? UploadTotal,
    long? DownloadTotal,
    IReadOnlyList<ConnectionSummary>? Connections,
    TrafficStatisticsSnapshot? Statistics)
{
    public static readonly ConnectionStatisticsUpdate Empty = new(0, null, null, null, null);
}
