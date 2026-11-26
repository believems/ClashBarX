using ClashBarX.App.Core;
using ClashBarX.Core.Models;

namespace ClashBarX.App.Tests.Core;

/// <summary>连接页模型规格测试（源：ConnectionsViewModel + ConnectionsTabView 行语义）。</summary>
public class ConnectionsPageViewModelTests
{
    private static ConnectionSummary Connection(
        string id,
        long upload,
        long download,
        string host = "example.com",
        string network = "tcp",
        string rule = "MATCH",
        string? rulePayload = null,
        string[]? chains = null,
        double? startTimestamp = null) =>
        new(id, upload, download, "2026-01-01T00:00:00Z", startTimestamp, rule, rulePayload,
            chains,
            new ConnectionMetadata(network, "127.0.0.1", 12345, "93.184.216.34", host,
                null, "app", "C:\\app.exe"));

    private static ConnectionStatisticsUpdate Update(
        IReadOnlyList<ConnectionSummary> connections,
        int totalCount) =>
        new(totalCount, 1, 2, connections, null);

    private sealed class Harness
    {
        public double Now;
        public List<string> ClosedIds = new();
        public List<string> Logs = new();
        public int CloseAllCalls;
        public ConnectionsPageViewModel ViewModel = null!;

        public Harness() => Reset();

        public void Reset()
        {
            Now = 0;
            ViewModel = new ConnectionsPageViewModel(
                id =>
                {
                    ClosedIds.Add(id);
                    return Task.CompletedTask;
                },
                () =>
                {
                    CloseAllCalls++;
                    return Task.CompletedTask;
                },
                message => Logs.Add(message),
                () => Now);
        }
    }

    [Fact]
    public void 快照生成行文本()
    {
        var harness = new Harness();
        var connection = Connection("c1", 1024, 2048, host: "example.com", rule: "GEOIP",
            rulePayload: "CN", chains: new[] { "节点A", "组1" }, startTimestamp: 1767225600);

        harness.ViewModel.IngestSnapshot(Update(new[] { connection }, 1));

        ConnectionRowViewModel row = Assert.Single(harness.ViewModel.Connections);
        Assert.Equal("example.com", row.Host);
        Assert.Equal("GEOIP(CN)", row.Rule);   // payload 优先
        Assert.Equal("TCP", row.Network);
        Assert.Equal("组1 › 节点A", row.Chain);   // chains 反转（叶端在前）
        Assert.Equal("1.0KB", row.Upload);
        Assert.Equal("2.0KB", row.Download);
        Assert.Equal("1/1", harness.ViewModel.SummaryText);
    }

    [Fact]
    public void 计数徽章按保留上限截断()
    {
        var harness = new Harness();
        List<ConnectionSummary> connections = Enumerable.Range(0, 200)
            .Select(i => Connection("c" + i, 0, 0))
            .ToList();

        harness.ViewModel.IngestSnapshot(Update(connections, totalCount: 500));

        Assert.Equal(120, harness.ViewModel.Connections.Count);
        Assert.Equal("120/120", harness.ViewModel.SummaryText);   // total clamp 到 120
    }

    [Fact]
    public void 速率差分与平滑()
    {
        var harness = new Harness();
        harness.ViewModel.IngestSnapshot(Update(new[] { Connection("c1", 100, 200) }, 1));

        // 1 秒后：差值 100/200，新值权重 0.5（旧速率 0）
        harness.Now = 1.0;
        harness.ViewModel.IngestSnapshot(Update(new[] { Connection("c1", 200, 400) }, 1));

        ConnectionRowViewModel row = Assert.Single(harness.ViewModel.Connections);
        Assert.Equal(FormatExpected(50) + "/s", row.UploadRate);   // 0.5*100/1 + 0.5*0
        Assert.Equal(FormatExpected(100) + "/s", row.DownloadRate);

        static string FormatExpected(double rate) =>
            rate < 1 ? "–" : ConnectionsPageViewModel.FormatBytes((long)rate);
    }

    [Fact]
    public void 采样间隔不足沿用旧样本()
    {
        var harness = new Harness();
        harness.ViewModel.IngestSnapshot(Update(new[] { Connection("c1", 100, 100) }, 1));

        harness.Now = 0.1;   // < 0.2s 最低采样间隔
        harness.ViewModel.IngestSnapshot(Update(new[] { Connection("c1", 10000, 10000) }, 1));

        ConnectionRowViewModel row = Assert.Single(harness.ViewModel.Connections);
        Assert.Equal("–", row.UploadRate);   // 沿用首帧速率 0
    }

    [Fact]
    public void 文本过滤按host匹配()
    {
        var harness = new Harness();
        harness.ViewModel.IngestSnapshot(Update(new[]
        {
            Connection("c1", 0, 0, host: "google.com"),
            Connection("c2", 0, 0, host: "example.com"),
        }, 2));

        harness.ViewModel.FilterText = "google";

        ConnectionRowViewModel row = Assert.Single(harness.ViewModel.Connections);
        Assert.Equal("google.com", row.Host);

        harness.ViewModel.FilterText = string.Empty;
        Assert.Equal(2, harness.ViewModel.Connections.Count);
    }

    [Fact]
    public void 传输过滤()
    {
        var harness = new Harness();
        harness.ViewModel.IngestSnapshot(Update(new[]
        {
            Connection("c1", 0, 0, network: "tcp"),
            Connection("c2", 0, 0, network: "udp"),
            Connection("c3", 0, 0, network: "quic"),
        }, 3));

        harness.ViewModel.TransportFilter = ConnectionsTransportFilter.Udp;
        Assert.Equal("c2", Assert.Single(harness.ViewModel.Connections).Id);

        harness.ViewModel.TransportFilter = ConnectionsTransportFilter.Other;   // 非空且非 tcp/udp
        Assert.Equal("c3", Assert.Single(harness.ViewModel.Connections).Id);

        harness.ViewModel.TransportFilter = ConnectionsTransportFilter.All;
        Assert.Equal(3, harness.ViewModel.Connections.Count);
    }

    [Fact]
    public void 排序_下载流量降序与升序()
    {
        var harness = new Harness();
        harness.ViewModel.SortKey = ConnectionsSortKey.Download;
        harness.ViewModel.IngestSnapshot(Update(new[]
        {
            Connection("c1", 0, 100),
            Connection("c2", 0, 500),
            Connection("c3", 0, 300),
        }, 3));

        Assert.Equal(new[] { "c2", "c3", "c1" },
            harness.ViewModel.Connections.Select(row => row.Id).ToArray());

        harness.ViewModel.IsSortDescending = false;
        Assert.Equal(new[] { "c1", "c3", "c2" },
            harness.ViewModel.Connections.Select(row => row.Id).ToArray());
    }

    [Fact]
    public void 排序_默认保持内核序()
    {
        var harness = new Harness();
        harness.ViewModel.IngestSnapshot(Update(new[]
        {
            Connection("c1", 0, 100),
            Connection("c2", 0, 500),
        }, 2));

        harness.ViewModel.SortKey = ConnectionsSortKey.Total;   // 先离开默认，再回来
        harness.ViewModel.SortKey = ConnectionsSortKey.Default;

        Assert.Equal(new[] { "c1", "c2" },
            harness.ViewModel.Connections.Select(row => row.Id).ToArray());
    }

    [Fact]
    public void 悬停期间已显示行保持位置()
    {
        var harness = new Harness();
        harness.ViewModel.SortKey = ConnectionsSortKey.Download;
        harness.ViewModel.IngestSnapshot(Update(new[]
        {
            Connection("c1", 0, 100),
            Connection("c2", 0, 200),
        }, 2));
        Assert.Equal(new[] { "c2", "c1" },
            harness.ViewModel.Connections.Select(row => row.Id).ToArray());

        harness.ViewModel.IsReorderingPaused = true;
        // c1 流量反超：排序结果应为 c1 在前，但暂停期间已显示行保位、新行尾插
        harness.ViewModel.IngestSnapshot(Update(new[]
        {
            Connection("c1", 0, 5000),
            Connection("c2", 0, 200),
        }, 2));

        Assert.Equal(new[] { "c2", "c1" },
            harness.ViewModel.Connections.Select(row => row.Id).ToArray());
    }

    [Fact]
    public void 关闭命令传递连接ID()
    {
        var harness = new Harness();
        harness.ViewModel.IngestSnapshot(Update(new[] { Connection("c1", 0, 0) }, 1));

        ConnectionRowViewModel row = harness.ViewModel.Connections[0];
        Assert.NotNull(row.CloseConnectionCommand);
        row.CloseConnectionCommand!.Execute(row);

        Assert.Equal(new[] { "c1" }, harness.ClosedIds);
    }

    [Fact]
    public void 无连接数据的快照保持当前列表()
    {
        var harness = new Harness();
        harness.ViewModel.IngestSnapshot(Update(new[] { Connection("c1", 10, 10) }, 1));

        // Tab 不可见时 Process 不带 connections：列表保持、计数仍更新
        harness.ViewModel.IngestSnapshot(new ConnectionStatisticsUpdate(42, 1, 2, null, null));

        Assert.Equal("c1", Assert.Single(harness.ViewModel.Connections).Id);
        Assert.Equal("1/42", harness.ViewModel.SummaryText);
    }

    [Theory]
    [InlineData(0, "0B")]
    [InlineData(512, "512B")]
    [InlineData(1024, "1.0KB")]
    [InlineData(1024 * 1024 * 5, "5.0MB")]
    [InlineData(1024L * 1024 * 1024 * 3, "3.0GB")]
    public void 字节格式化(long bytes, string expected)
    {
        Assert.Equal(expected, ConnectionsPageViewModel.FormatBytes(bytes));
    }

    [Fact]
    public void 规则与链路文本回退()
    {
        Assert.Equal("--", ConnectionsPageViewModel.RuleText(Connection("c", 0, 0, rule: "MATCH")));
        Assert.Equal("--", ConnectionsPageViewModel.RuleText(Connection("c", 0, 0, rule: "FINAL")));
        Assert.Equal("GEOIP", ConnectionsPageViewModel.RuleText(Connection("c", 0, 0, rule: "GEOIP")));
        Assert.Equal("-", ConnectionsPageViewModel.ChainText(Connection("c", 0, 0, chains: null)));

        Assert.Equal("93.184.216.34", ConnectionsPageViewModel.HostText(Connection("c", 0, 0, host: "")));   // 空 host 回退 destIP
    }

    [Fact]
    public void 速率格式化_小于1B每秒视为空闲()
    {
        Assert.Equal("–", ConnectionsPageViewModel.FormatRate(0.5));
        Assert.Equal("100B/s", ConnectionsPageViewModel.FormatRate(100));
    }

    // ---- 三维度流量统计（M3-4：源 TrafficStatisticsView + ConnectionsStore 的统计态） ----

    [Fact]
    public void 统计快照喂入后按维度重建行()
    {
        var harness = new Harness();
        harness.ViewModel.ShowsStatistics = true;
        var statistics = new TrafficStatisticsSnapshot(
            TrafficStatisticsDimension.Node,
            new[]
            {
                new TrafficStatisticsEntry("节点A", "节点A", 2048, 4096),
                new TrafficStatisticsEntry("节点B", "节点B", 512, 256),
            });

        harness.ViewModel.IngestSnapshot(new ConnectionStatisticsUpdate(2, 1, 2, null, statistics));

        Assert.Equal(2, harness.ViewModel.StatisticsRows.Count);
        Assert.Equal("节点A", harness.ViewModel.StatisticsRows[0].Name);
        Assert.Equal("6.0KB", harness.ViewModel.StatisticsRows[0].TotalText);   // Core 已按 total 降序
        Assert.Equal("2.0KB", harness.ViewModel.StatisticsRows[0].UploadText);
        Assert.Equal("4.0KB", harness.ViewModel.StatisticsRows[0].DownloadText);
    }

    [Fact]
    public void 维度不一致的快照不刷新统计行()
    {
        var harness = new Harness();
        harness.ViewModel.ShowsStatistics = true;
        var domainStatistics = new TrafficStatisticsSnapshot(
            TrafficStatisticsDimension.Domain,
            new[] { new TrafficStatisticsEntry("a.com", "a.com", 100, 100) });

        // 默认 node 维度：domain 快照丢弃（源：dimension 一致才入 store）
        harness.ViewModel.IngestSnapshot(new ConnectionStatisticsUpdate(1, 1, 2, null, domainStatistics));
        Assert.Empty(harness.ViewModel.StatisticsRows);

        // 切到 domain 维度后重喂才显示
        harness.ViewModel.StatisticsDimension = TrafficStatisticsDimension.Domain;
        harness.ViewModel.IngestSnapshot(new ConnectionStatisticsUpdate(1, 1, 2, null, domainStatistics));
        Assert.Single(harness.ViewModel.StatisticsRows);
        Assert.Equal("a.com", harness.ViewModel.StatisticsRows[0].Name);
    }

    [Fact]
    public void 空名统计条目显示未识别()
    {
        var harness = new Harness();
        harness.ViewModel.ShowsStatistics = true;
        var statistics = new TrafficStatisticsSnapshot(
            TrafficStatisticsDimension.Application,
            new[] { new TrafficStatisticsEntry("", "", 10, 10) });

        harness.ViewModel.StatisticsDimension = TrafficStatisticsDimension.Application;
        harness.ViewModel.IngestSnapshot(new ConnectionStatisticsUpdate(1, 1, 2, null, statistics));

        Assert.Equal("未识别", harness.ViewModel.StatisticsRows[0].Name);
    }

    [Fact]
    public void 统计视图关闭时不重建行()
    {
        var harness = new Harness();
        var statistics = new TrafficStatisticsSnapshot(
            TrafficStatisticsDimension.Node,
            new[] { new TrafficStatisticsEntry("节点A", "节点A", 100, 100) });

        // ShowsStatistics=false：快照喂入但不重建（源：仅统计视图可见才计算）
        harness.ViewModel.IngestSnapshot(new ConnectionStatisticsUpdate(1, 1, 2, null, statistics));
        Assert.Empty(harness.ViewModel.StatisticsRows);

        // 打开统计视图：已存快照重建成行
        harness.ViewModel.ShowsStatistics = true;
        harness.ViewModel.IngestSnapshot(new ConnectionStatisticsUpdate(1, 1, 2, null, statistics));
        Assert.Single(harness.ViewModel.StatisticsRows);
    }
}
