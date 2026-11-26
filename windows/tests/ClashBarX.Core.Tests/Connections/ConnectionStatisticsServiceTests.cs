using System.Text;
using ClashBarX.Core.Connections;
using ClashBarX.Core.Models;

namespace ClashBarX.Core.Tests.Connections;

/// <summary>连接流差分统计规格测试（源：Services/ConnectionStatisticsService.swift）。</summary>
public class ConnectionStatisticsServiceTests
{
    private static ReadOnlyMemory<byte> Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private static string Frame(params string[] connections) =>
        "{\"connections\":[" + string.Join(",", connections) + "],\"uploadTotal\":1,\"downloadTotal\":2}";

    /// <summary>路径内插进 JSON 前转义反斜杠。</summary>
    private static string JsonEscape(string value) => value.Replace("\\", "\\\\");

    private static string Connection(
        string id,
        long upload,
        long download,
        string host,
        string process,
        string processPath,
        string chain = "\"节点A\",\"组1\"") =>
        "{\"id\":\"" + id + "\",\"upload\":" + upload + ",\"download\":" + download +
        ",\"start\":\"2026-01-01T00:00:00Z\",\"rule\":\"MATCH\"" +
        ",\"chains\":[" + chain + "]" +
        ",\"metadata\":{\"host\":\"" + host + "\",\"process\":\"" + process +
        "\",\"processPath\":\"" + JsonEscape(processPath) + "\"}}";

    private static string ConnectionRaw(
        string id,
        long upload,
        string host,
        string sniffHost,
        string destinationIP,
        string process = "",
        string processPath = "") =>
        "{\"id\":\"" + id + "\",\"upload\":" + upload + ",\"download\":0,\"chains\":[\"节点A\"]," +
        "\"metadata\":{\"host\":\"" + host + "\",\"sniffHost\":\"" + sniffHost +
        "\",\"destinationIP\":\"" + destinationIP + "\",\"process\":\"" + process +
        "\",\"processPath\":\"" + JsonEscape(processPath) + "\"}}";

    [Fact]
    public void 首帧只建基线不计统计()
    {
        var service = new ConnectionStatisticsService();

        ConnectionStatisticsUpdate update = service.Process(
            Bytes(Frame(Connection("c1", 100, 200, "a.com", "app", "C:\\apps\\app.exe"))),
            includeConnections: true,
            statisticsDimension: TrafficStatisticsDimension.Node);

        Assert.NotNull(update.Connections);
        ConnectionSummary connection = Assert.Single(update.Connections);
        Assert.Equal("c1", connection.Id);
        Assert.NotNull(connection.StartTimestamp);   // includeConnections 时解析 start
        Assert.Equal(1, update.TotalCount);
        Assert.NotNull(update.Statistics);
        Assert.Empty(update.Statistics.Entries);   // 首帧无基线差值，统计为空
    }

    [Fact]
    public void 第二帧差值计入三维度统计()
    {
        var service = new ConnectionStatisticsService();
        service.Process(Bytes(Frame(Connection("c1", 100, 200, "a.com", "app", "C:\\apps\\app.exe"))),
            includeConnections: false);

        ConnectionStatisticsUpdate update = service.Process(
            Bytes(Frame(Connection("c1", 300, 500, "a.com", "app", "C:\\apps\\app.exe"))),
            includeConnections: false,
            statisticsDimension: TrafficStatisticsDimension.Node);

        TrafficStatisticsEntry entry = Assert.Single(update.Statistics!.Entries);
        Assert.Equal("节点A", entry.Id);   // 节点 = chains 首元素（叶端）
        Assert.Equal(200, entry.Upload);   // 300 - 100
        Assert.Equal(300, entry.Download);   // 500 - 200
        Assert.Equal(500, entry.Total);
    }

    [Fact]
    public void 无差值的帧不计入统计()
    {
        var service = new ConnectionStatisticsService();
        var frame = Bytes(Frame(Connection("c1", 100, 200, "a.com", "app", "C:\\apps\\app.exe")));
        service.Process(frame, includeConnections: false);

        ConnectionStatisticsUpdate update = service.Process(
            frame,
            includeConnections: false,
            statisticsDimension: TrafficStatisticsDimension.Domain);

        Assert.NotNull(update.Statistics);
        Assert.Empty(update.Statistics.Entries);
    }

    [Fact]
    public void 计数回退时差值按0截断()
    {
        var service = new ConnectionStatisticsService();
        service.Process(Bytes(Frame(Connection("c1", 500, 500, "a.com", "app", "C:\\apps\\app.exe"))),
            includeConnections: false);

        // 计数回退（新值小于基线）：差值为负按 0 处理，整帧无流量
        ConnectionStatisticsUpdate update = service.Process(
            Bytes(Frame(Connection("c1", 100, 100, "a.com", "app", "C:\\apps\\app.exe"))),
            includeConnections: false,
            statisticsDimension: TrafficStatisticsDimension.Domain);

        Assert.Empty(update.Statistics!.Entries);
    }

    [Fact]
    public void 应用维度_显示名回退到进程路径文件名()
    {
        var service = new ConnectionStatisticsService();

        service.Process(Bytes(Frame(ConnectionRaw("c1", 0, "a.com", "", "", process: "",
            processPath: "C:\\Program Files\\Tool\\tool.exe"))), includeConnections: false);
        ConnectionStatisticsUpdate update = service.Process(
            Bytes(Frame(ConnectionRaw("c1", 100, "a.com", "", "", process: "",
                processPath: "C:\\Program Files\\Tool\\tool.exe"))),
            includeConnections: false,
            statisticsDimension: TrafficStatisticsDimension.Application);

        TrafficStatisticsEntry entry = Assert.Single(update.Statistics!.Entries);
        Assert.Equal("C:\\Program Files\\Tool\\tool.exe", entry.Id);   // 聚合键用完整路径
        Assert.Equal("tool.exe", entry.Name);   // 显示名取路径文件名（源：lastPathComponent 回退）
    }

    [Fact]
    public void 域名维度_host缺失回退sniffHost再回退destinationIP()
    {
        var sniffService = new ConnectionStatisticsService();
        sniffService.Process(
            Bytes(Frame(ConnectionRaw("c1", 0, "a.com", "sniffed.example", "1.2.3.4"))),
            includeConnections: false);
        ConnectionStatisticsUpdate sniffUsed = sniffService.Process(
            Bytes(Frame(ConnectionRaw("c1", 50, "a.com", "sniffed.example", "1.2.3.4"))),
            includeConnections: false,
            statisticsDimension: TrafficStatisticsDimension.Domain);
        Assert.Equal("a.com", Assert.Single(sniffUsed.Statistics!.Entries).Id);   // host 存在优先

        var noHostService = new ConnectionStatisticsService();
        noHostService.Process(
            Bytes(Frame(ConnectionRaw("c1", 0, "", "sniffed.example", "1.2.3.4"))),
            includeConnections: false);
        ConnectionStatisticsUpdate sniffFallback = noHostService.Process(
            Bytes(Frame(ConnectionRaw("c1", 50, "", "sniffed.example", "1.2.3.4"))),
            includeConnections: false,
            statisticsDimension: TrafficStatisticsDimension.Domain);
        Assert.Equal("sniffed.example", Assert.Single(sniffFallback.Statistics!.Entries).Id);

        var noSniffService = new ConnectionStatisticsService();
        noSniffService.Process(
            Bytes(Frame(ConnectionRaw("c1", 0, "", "", "1.2.3.4"))),
            includeConnections: false);
        ConnectionStatisticsUpdate destFallback = noSniffService.Process(
            Bytes(Frame(ConnectionRaw("c1", 50, "", "", "1.2.3.4"))),
            includeConnections: false,
            statisticsDimension: TrafficStatisticsDimension.Domain);
        Assert.Equal("1.2.3.4", Assert.Single(destFallback.Statistics!.Entries).Id);
    }

    [Fact]
    public void 统计条目按总量降序再按名称升序()
    {
        var service = new ConnectionStatisticsService();
        string baseline = Frame(
            Connection("c1", 0, 0, "a.com", "p1", "C:\\p1.exe", chain: "\"节点B\",\"组1\""),
            Connection("c2", 0, 0, "b.com", "p2", "C:\\p2.exe", chain: "\"节点A\",\"组1\""),
            Connection("c3", 0, 0, "c.com", "p3", "C:\\p3.exe", chain: "\"节点C\",\"组1\""));
        string grown = Frame(
            Connection("c1", 100, 0, "a.com", "p1", "C:\\p1.exe", chain: "\"节点B\",\"组1\""),
            Connection("c2", 300, 0, "b.com", "p2", "C:\\p2.exe", chain: "\"节点A\",\"组1\""),
            Connection("c3", 300, 0, "c.com", "p3", "C:\\p3.exe", chain: "\"节点C\",\"组1\""));

        service.Process(Bytes(baseline), includeConnections: false);
        ConnectionStatisticsUpdate update = service.Process(
            Bytes(grown),
            includeConnections: false,
            statisticsDimension: TrafficStatisticsDimension.Node);

        // 节点A 与节点C 并列 300（名称升序），节点B 100 垫底
        Assert.Equal(new[] { "节点A", "节点C", "节点B" },
            update.Statistics!.Entries.Select(entry => entry.Id).ToArray());
    }

    [Fact]
    public void 连接消失后重现会从头累计()
    {
        var service = new ConnectionStatisticsService();
        service.Process(Bytes(Frame(Connection("c1", 100, 100, "a.com", "app", "C:\\app.exe"))),
            includeConnections: false);

        ConnectionStatisticsUpdate grown = service.Process(
            Bytes(Frame(Connection("c1", 200, 200, "a.com", "app", "C:\\app.exe"))),
            includeConnections: false,
            statisticsDimension: TrafficStatisticsDimension.Node);
        Assert.Equal(200, TotalOf(grown));   // 差值 100 + 100

        // c1 在空帧中消失：基线被替换，重现后 previous 缺失按 0 差分（源：previous ?? 0）
        service.Process(Bytes("{\"connections\":[]}"), includeConnections: false);
        ConnectionStatisticsUpdate reappeared = service.Process(
            Bytes(Frame(Connection("c1", 50, 50, "a.com", "app", "C:\\app.exe"))),
            includeConnections: false,
            statisticsDimension: TrafficStatisticsDimension.Node);
        Assert.Equal(300, TotalOf(reappeared));   // 200 + 差值 50 + 50

        static long TotalOf(ConnectionStatisticsUpdate update) =>
            update.Statistics!.Entries.Sum(entry => entry.Total);
    }

    [Fact]
    public void 未请求维度时统计为空()
    {
        var service = new ConnectionStatisticsService();
        service.Process(Bytes(Frame(Connection("c1", 0, 0, "a.com", "app", "C:\\app.exe"))),
            includeConnections: false);

        ConnectionStatisticsUpdate update = service.Process(
            Bytes(Frame(Connection("c1", 100, 100, "a.com", "app", "C:\\app.exe"))),
            includeConnections: true);
        Assert.Null(update.Statistics);
        Assert.NotNull(update.Connections);
    }

    [Fact]
    public void includeConnections为false时跳过列表输出()
    {
        var service = new ConnectionStatisticsService();

        ConnectionStatisticsUpdate update = service.Process(
            Bytes(Frame(Connection("c1", 100, 200, "a.com", "app", "C:\\app.exe"))),
            includeConnections: false);

        Assert.Null(update.Connections);
        Assert.Equal(1, update.TotalCount);   // 计数仍统计全量
    }

    [Fact]
    public void 并发处理不损坏内部状态()
    {
        var service = new ConnectionStatisticsService();
        service.Process(Bytes("{\"connections\":[]}"), includeConnections: false);

        Parallel.For(0, 200, i => service.Process(
            Bytes(Frame(Connection("c" + i % 4, i * 100, i * 100, "a.com", "app", "C:\\app.exe"))),
            includeConnections: false,
            statisticsDimension: TrafficStatisticsDimension.Node));

        ConnectionStatisticsUpdate update = service.Process(
            Bytes("{\"connections\":[]}"),
            includeConnections: false,
            statisticsDimension: TrafficStatisticsDimension.Node);
        Assert.NotNull(update.Statistics);
    }
}
