using System.Text.Json;
using ClashBarX.Core.Json;
using ClashBarX.Core.Models;

namespace ClashBarX.Core.Connections;

/// <summary>
/// 连接流统计（源：Services/ConnectionStatisticsService.swift）。以帧间差值聚合
/// 节点/应用/域名三维度流量；mihomo 每帧上报的是各连接累计字节，速率即差值语义。
/// 回调来自后台线程，内部加锁保证串行语义（对齐 Swift actor 隔离）。
/// </summary>
public sealed class ConnectionStatisticsService
{
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = JsonSharedOptions.Json;

    private Dictionary<string, (long Upload, long Download)> _activeConnections = new();
    private Dictionary<string, TrafficStatisticsEntry> _nodeTotals = new();
    private Dictionary<string, TrafficStatisticsEntry> _applicationTotals = new();
    private Dictionary<string, TrafficStatisticsEntry> _domainTotals = new();
    private bool _hasBaseline;

    /// <summary>
    /// 处理一帧 /connections 快照。首帧只建基线不计统计；<paramref name="includeConnections"/>
    /// 为 false 时跳过连接列表（并跳过 start 时间戳解析）以省开销；
    /// <paramref name="statisticsDimension"/> 非空时附带该维度的聚合快照。
    /// </summary>
    public ConnectionStatisticsUpdate Process(
        ReadOnlyMemory<byte> payload,
        bool includeConnections,
        TrafficStatisticsDimension? statisticsDimension = null)
    {
        ConnectionsSnapshot snapshot = JsonSerializer
            .Deserialize<ConnectionsSnapshot>(payload.Span, _json) ?? ConnectionsSnapshot.Empty;

        var nextActive = new Dictionary<string, (long, long)>(capacity: Math.Max(snapshot.TotalCount, 1));

        lock (_gate)
        {
            foreach (ConnectionSummary connection in snapshot.Connections)
            {
                long upload = Math.Max(0, connection.Upload ?? 0);
                long download = Math.Max(0, connection.Download ?? 0);
                nextActive[connection.Id] = (upload, download);
                if (!_hasBaseline)
                {
                    continue;
                }

                _activeConnections.TryGetValue(connection.Id, out (long Upload, long Download) previous);
                long uploadDelta = Math.Max(0, upload - previous.Upload);
                long downloadDelta = Math.Max(0, download - previous.Download);
                if (uploadDelta <= 0 && downloadDelta <= 0)
                {
                    continue;
                }

                // 节点 = 链路叶端（chains 首元素，即实际出口节点）
                string node = connection.Chains is { Count: > 0 } chains ? chains[0] : "";
                Accumulate(_nodeTotals, node, node, uploadDelta, downloadDelta);

                ConnectionMetadata? metadata = connection.Metadata;
                string? processPath = metadata?.ProcessPath;
                string? process = metadata?.Process;
                string application = processPath ?? process ?? "";
                string applicationName = process ?? Path.GetFileName(processPath ?? "") ?? "";
                Accumulate(_applicationTotals, application, applicationName, uploadDelta, downloadDelta);

                string domain = metadata?.Host ?? metadata?.SniffHost ?? metadata?.DestinationIP ?? "";
                Accumulate(_domainTotals, domain, domain, uploadDelta, downloadDelta);
            }

            _activeConnections = nextActive;
            _hasBaseline = true;

            IReadOnlyList<ConnectionSummary>? connections = null;
            if (includeConnections)
            {
                connections = snapshot.Connections
                    .Select(item => item with { StartTimestamp = ConnectionSummary.ParseTimestamp(item.Start) })
                    .ToList();
            }

            TrafficStatisticsSnapshot? statistics = statisticsDimension is { } dimension
                ? BuildStatistics(dimension)
                : null;

            return new ConnectionStatisticsUpdate(
                TotalCount: snapshot.TotalCount,
                UploadTotal: snapshot.UploadTotal,
                DownloadTotal: snapshot.DownloadTotal,
                Connections: connections,
                Statistics: statistics);
        }
    }

    private TrafficStatisticsSnapshot BuildStatistics(TrafficStatisticsDimension dimension)
    {
        Dictionary<string, TrafficStatisticsEntry> totals = dimension switch
        {
            TrafficStatisticsDimension.Node => _nodeTotals,
            TrafficStatisticsDimension.Application => _applicationTotals,
            TrafficStatisticsDimension.Domain => _domainTotals,
            _ => _domainTotals,
        };

        List<TrafficStatisticsEntry> entries = totals.Values
            .OrderByDescending(entry => entry.Total)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();
        return new TrafficStatisticsSnapshot(dimension, entries);
    }

    private static void Accumulate(
        Dictionary<string, TrafficStatisticsEntry> totals,
        string id,
        string name,
        long uploadDelta,
        long downloadDelta)
    {
        totals.TryGetValue(id, out TrafficStatisticsEntry? previous);
        TrafficStatisticsEntry basis = previous ?? new TrafficStatisticsEntry(id, name, 0, 0);
        totals[id] = basis with
        {
            Upload = basis.Upload + uploadDelta,
            Download = basis.Download + downloadDelta,
        };
    }
}
