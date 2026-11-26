using System.Text.Json;
using ClashBarX.Core.Json;
using ClashBarX.Core.Models;

namespace ClashBarX.Core.Tests.Models;

/// <summary>模型解码规格测试（源：Sources/ClashBar/Models/ 下各 Swift 模型的容错语义）。</summary>
public class ModelDecodingTests
{
    private static T Deserialize<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, JsonSharedOptions.Json)!;

    // ---- Runtime.swift ----

    [Fact]
    public void 版本信息_正常解码()
    {
        var info = Deserialize<VersionInfo>("{\"version\":\"v1.18.8\"}");
        Assert.Equal("v1.18.8", info.Version);
    }

    [Fact]
    public void 版本信息_缺失version抛异常()
    {
        Assert.Throws<JsonException>(() => Deserialize<VersionInfo>("{}"));
    }

    [Fact]
    public void 内核升级响应_字段可缺省()
    {
        var response = Deserialize<CoreUpgradeResponse>("{}");
        Assert.Null(response.Status);
        Assert.Null(response.Message);

        response = Deserialize<CoreUpgradeResponse>("{\"status\":\"success\",\"message\":\"done\"}");
        Assert.Equal("success", response.Status);
        Assert.Equal("done", response.Message);
    }

    [Fact]
    public void 流量快照_字段缺失按0并支持多键回退()
    {
        var snapshot = Deserialize<TrafficSnapshot>("{\"up\":100,\"down\":200}");
        Assert.Equal(100, snapshot.Up);
        Assert.Equal(200, snapshot.Down);
        Assert.Null(snapshot.UpTotal);
        Assert.Null(snapshot.DownTotal);

        snapshot = Deserialize<TrafficSnapshot>("{}");
        Assert.Equal(0, snapshot.Up);
        Assert.Equal(0, snapshot.Down);

        snapshot = Deserialize<TrafficSnapshot>("{\"up\":1,\"down\":2,\"uptotal\":3,\"downtotal\":4}");
        Assert.Equal(3, snapshot.UpTotal);
        Assert.Equal(4, snapshot.DownTotal);

        snapshot = Deserialize<TrafficSnapshot>("{\"up\":1,\"down\":2,\"uploadTotal\":30,\"downloadTotal\":40}");
        Assert.Equal(30, snapshot.UpTotal);
        Assert.Equal(40, snapshot.DownTotal);

        snapshot = Deserialize<TrafficSnapshot>("{\"up\":1,\"down\":2,\"upTotal\":300,\"downTotal\":400}");
        Assert.Equal(300, snapshot.UpTotal);
        Assert.Equal(400, snapshot.DownTotal);
    }

    [Fact]
    public void 流量快照_字段类型不符抛异常()
    {
        Assert.Throws<JsonException>(() => Deserialize<TrafficSnapshot>("{\"up\":\"100\"}"));
    }

    [Fact]
    public void 内存快照_缺失inuse按0()
    {
        Assert.Equal(123456, Deserialize<MemorySnapshot>("{\"inuse\":123456}").Inuse);
        Assert.Equal(0, Deserialize<MemorySnapshot>("{}").Inuse);
    }

    // ---- Log.swift ----

    [Fact]
    public void 日志行_字段可缺省()
    {
        var line = Deserialize<LogLine>("{\"type\":\"warning\",\"payload\":\"connect timeout\"}");
        Assert.Equal("warning", line.Type);
        Assert.Equal("connect timeout", line.Payload);

        line = Deserialize<LogLine>("{\"payload\":\"仅消息\"}");
        Assert.Null(line.Type);
    }

    [Fact]
    public void 日志批量响应_正常解码()
    {
        var response = Deserialize<LogsResponse>("{\"logs\":[{\"type\":\"info\",\"payload\":\"a\"}]}");
        Assert.Single(response.Logs!);
        Assert.Equal("a", response.Logs![0].Payload);
    }

    [Theory]
    [InlineData("{\"delay\":120}", 120)]
    [InlineData("{\"unknown\":130}", 130)]
    [InlineData("{\"delay\":null}", null)]
    [InlineData("{\"foo\":\"x\"}", null)]
    [InlineData("{}", null)]
    public void 单节点测速_按规则解码(string json, int? expected)
    {
        Assert.Equal(expected, Deserialize<DelayMeasurement>(json).Value);
    }

    [Fact]
    public void 组测速_对象解码为字典()
    {
        var measurement = Deserialize<GroupDelayMeasurement>("{\"节点A\":123,\"节点B\":456}");
        Assert.Equal(2, measurement.Values.Count);
        Assert.Equal(123, measurement.Values["节点A"]);
        Assert.Equal(456, measurement.Values["节点B"]);
    }

    [Fact]
    public void 组测速_任何不匹配的值都折叠为空表()
    {
        Assert.Empty(Deserialize<GroupDelayMeasurement>("{\"节点A\":null}").Values);
        Assert.Empty(Deserialize<GroupDelayMeasurement>("{\"节点A\":\"120\"}").Values);
    }

    // ---- Connection.swift ----

    [Fact]
    public void 连接摘要_多键回退解码()
    {
        var summary = Deserialize<ConnectionSummary>("""
            {
              "id": "c1",
              "upload": 100,
              "download": 200,
              "start": "2026-11-26T09:30:00.123Z",
              "rule": "GEOIP,CN",
              "rule_payload": "CN",
              "chains": ["节点A", "选择组"],
              "metadata": {
                "network": "tcp",
                "source_ip": "127.0.0.1",
                "source_port": "54321",
                "destination_ip": "1.2.3.4",
                "destinationHost": "example.com",
                "process": "chrome.exe",
                "processPath": "C:/chrome.exe"
              }
            }
            """);

        Assert.Equal("c1", summary.Id);
        Assert.Equal(100, summary.Upload);
        Assert.Equal(200, summary.Download);
        Assert.Equal("CN", summary.RulePayload);
        Assert.Equal(new[] { "节点A", "选择组" }, summary.Chains!.ToArray());
        Assert.Equal("tcp", summary.Metadata!.Network);
        Assert.Equal("127.0.0.1", summary.Metadata.SourceIP);
        Assert.Equal(54321, summary.Metadata.SourcePort);
        Assert.Equal("1.2.3.4", summary.Metadata.DestinationIP);
        Assert.Equal("example.com", summary.Metadata.Host);
        Assert.Equal("chrome.exe", summary.Metadata.Process);
        Assert.Equal("C:/chrome.exe", summary.Metadata.ProcessPath);
        // 单条反序列化时解析时间戳
        Assert.NotNull(summary.StartTimestamp);
    }

    [Fact]
    public void 连接摘要_chains单值回退与payload键回退()
    {
        var summary = Deserialize<ConnectionSummary>("{\"id\":\"c2\",\"payload\":\"规则值\",\"chain\":\"单链\"}");
        Assert.Equal("规则值", summary.RulePayload);
        Assert.Equal(new[] { "单链" }, summary.Chains!.ToArray());
    }

    [Fact]
    public void 连接摘要_缺失id抛异常()
    {
        Assert.Throws<JsonException>(() => Deserialize<ConnectionSummary>("{\"upload\":1}"));
    }

    [Fact]
    public void 连接快照_保留上限与全量计数()
    {
        var connections = Enumerable.Range(0, 200)
            .Select(i => $"{{\"id\":\"c{i}\"}}")
            .ToArray();
        string json = $$"""{"downloadTotal":999,"uploadTotal":888,"memory":1024,"connections":[{{string.Join(",", connections)}}]}""";

        var snapshot = Deserialize<ConnectionsSnapshot>(json);

        Assert.Equal(200, snapshot.TotalCount);
        Assert.Equal(120, snapshot.Connections.Count);
        Assert.Equal("c0", snapshot.Connections[0].Id);
        Assert.Equal("c119", snapshot.Connections[119].Id);
        Assert.Equal(888, snapshot.UploadTotal);
        Assert.Equal(999, snapshot.DownloadTotal);
        // 快照内解析时不计算时间戳（源：parseStartTimestamp: false）
        Assert.All(snapshot.Connections, connection => Assert.Null(connection.StartTimestamp));
    }

    [Fact]
    public void 连接快照_缺失connections为空表()
    {
        var snapshot = Deserialize<ConnectionsSnapshot>("{}");
        Assert.Equal(0, snapshot.TotalCount);
        Assert.Empty(snapshot.Connections);
    }

    [Fact]
    public void 连接摘要_时间戳解析ISO8601()
    {
        var summary = Deserialize<ConnectionSummary>(
            "{\"id\":\"c3\",\"start\":\"2026-11-26T09:30:00.123456Z\"}");
        Assert.NotNull(summary.StartTimestamp);

        double expected = DateTimeOffset.Parse("2026-11-26T09:30:00.123456Z", System.Globalization.CultureInfo.InvariantCulture)
            .ToUnixTimeMilliseconds() / 1000.0;
        Assert.InRange(summary.StartTimestamp!.Value, expected - 0.001, expected + 0.001);
    }

    [Fact]
    public void 连接摘要_时间戳无效则为空()
    {
        var summary = Deserialize<ConnectionSummary>("{\"id\":\"c4\",\"start\":\"not a date\"}");
        Assert.Null(summary.StartTimestamp);
    }

    // ---- Proxy.swift ----

    [Fact]
    public void 代理组_容错解码()
    {
        var group = Deserialize<ProxyGroup>("""
            {
              "name": "选择组",
              "type": "Selector",
              "now": "香港 01",
              "all": ["香港 01", "香港 02"],
              "testUrl": " https://www.gstatic.com/generate_204 ",
              "timeout": "5000",
              "hidden": false,
              "history": [
                {"delay": 100},
                {"delay": 200},
                {"delay": 300},
                {"delay": 400},
                {"delay": 500},
                {"delay": 600},
                {"delay": 700}
              ]
            }
            """);

        Assert.Equal("选择组", group.Name);
        Assert.Equal("香港 01", group.Now);
        Assert.Equal(new[] { "香港 01", "香港 02" }, group.All.ToArray());
        Assert.Equal("https://www.gstatic.com/generate_204", group.TestUrl);
        Assert.Equal(5000, group.Timeout);          // 字符串整数可解
        Assert.False(group.Hidden);
        Assert.Equal(new[] { 300, 400, 500, 600, 700 }, group.DelayHistory.ToArray());   // 保留末尾 5 条
    }

    [Fact]
    public void 代理组_缺省值()
    {
        var group = Deserialize<ProxyGroup>("{}");
        Assert.Equal("Unknown", group.Name);
        Assert.Empty(group.All);
        Assert.Null(group.Timeout);
        Assert.Null(group.TestUrl);
    }

    [Fact]
    public void 代理组_非正timeout视为null()
    {
        Assert.Null(Deserialize<ProxyGroup>("{\"timeout\":0}").Timeout);
        Assert.Null(Deserialize<ProxyGroup>("{\"timeout\":-5}").Timeout);
        Assert.Equal(500, Deserialize<ProxyGroup>("{\"timeout\":500}").Timeout);
    }

    [Fact]
    public void 代理组集合_按键解码()
    {
        var response = Deserialize<ProxyGroupsResponse>(
            """{"proxies":{"选择组":{"name":"选择组","all":["a"]},"GLOBAL":{}}}""");
        Assert.Equal(2, response.Proxies.Count);
        Assert.Equal("Unknown", response.Proxies["GLOBAL"].Name);
    }

    [Fact]
    public void 配置快照_kebab键与tun容错()
    {
        var config = Deserialize<ConfigSnapshot>("""
            {
              "allow-lan": true,
              "mode": "rule",
              "log-level": "info",
              "mixed-port": 7890,
              "tun": { "enable": "yes", "stack": "gvisor" },
              "external-controller": "127.0.0.1:9090"
            }
            """);

        Assert.True(config.AllowLan);
        Assert.Equal("rule", config.Mode);
        Assert.Equal("info", config.LogLevel);
        Assert.Equal(7890, config.MixedPort);
        Assert.True(config.Tun!.Enable);
        Assert.Equal("gvisor", config.Tun.Stack);
        Assert.True(config.TunEnabled);
        Assert.Equal("127.0.0.1:9090", config.ExternalController);
    }

    // ---- Provider.swift ----

    [Fact]
    public void 订阅源详情_ruleCount多键严格回退()
    {
        Assert.Equal(100, Deserialize<ProviderDetail>("{\"ruleCount\":100}").RuleCount);
        Assert.Equal(200, Deserialize<ProviderDetail>("{\"rulesCount\":200}").RuleCount);
        Assert.Equal(300, Deserialize<ProviderDetail>("{\"count\":300}").RuleCount);
        Assert.Null(Deserialize<ProviderDetail>("{}").RuleCount);
        Assert.Throws<JsonException>(() => Deserialize<ProviderDetail>("{\"ruleCount\":\"500\"}"));
    }

    [Fact]
    public void 订阅源信息_大小写键回退()
    {
        var info = Deserialize<ProviderSubscriptionInfo>(
            """{"Upload":1024,"Download":2048,"total":4096,"expire":1735689600}""");
        Assert.Equal(1024, info.Upload);
        Assert.Equal(2048, info.Download);
        Assert.Equal(4096, info.Total);
        Assert.Equal(1735689600, info.Expire);
    }

    [Fact]
    public void 订阅源节点_缺省名与延迟历史()
    {
        var node = Deserialize<ProviderProxyNode>("{\"history\":[{\"delay\":80},{\"delay\":0}]}");
        Assert.Equal("-", node.Name);
        Assert.Equal(new[] { 80, 0 }, node.DelayHistory.ToArray());
    }

    [Fact]
    public void 订阅源详情_节点列表与订阅信息()
    {
        var detail = Deserialize<ProviderDetail>("""
            {
              "name": "sub1",
              "vehicleType": "HTTP",
              "updatedAt": "2026-11-01",
              "subscriptionInfo": {"upload": 1, "download": 2, "total": 3, "expire": 4},
              "proxies": [{"name": "节点A", "type": "ss"}, {"type": "vmess"}]
            }
            """);

        Assert.Equal("sub1", detail.Name);
        Assert.Equal("HTTP", detail.VehicleType);
        Assert.Equal(1, detail.SubscriptionInfo!.Upload);
        Assert.Equal(2, detail.SubscriptionInfo.Download);
        Assert.Equal(4, detail.SubscriptionInfo.Expire);
        Assert.Equal(2, detail.Proxies!.Count);
        Assert.Equal("节点A", detail.Proxies[0].Name);
        Assert.Equal("ss", detail.Proxies[0].Type);
        Assert.Equal("-", detail.Proxies[1].Name);
    }

    // ---- Rules.swift ----

    [Fact]
    public void 规则列表_正常解码()
    {
        var summary = Deserialize<RulesSummary>(
            """{"rules":[{"type":"DOMAIN-SUFFIX","payload":"google.com","proxy":"选择组"}]}""");
        Assert.Equal(1, summary.TotalCount);
        Assert.Equal("DOMAIN-SUFFIX", summary.Rules[0].Type);
        Assert.Equal("google.com", summary.Rules[0].Payload);
        Assert.Equal("选择组", summary.Rules[0].Proxy);
    }

    [Fact]
    public void 规则列表_缺失rules为空()
    {
        var summary = Deserialize<RulesSummary>("{}");
        Assert.Equal(0, summary.TotalCount);
        Assert.Empty(summary.Rules);
    }

    [Fact]
    public void 规则列表_超过20000条保留上限并计全量()
    {
        int total = RulesSummary.RetainedRuleLimit + 3;
        var rules = Enumerable.Range(0, total)
            .Select(i => $"{{\"type\":\"T\",\"payload\":\"{i}\",\"proxy\":\"P\"}}")
            .ToArray();
        string json = $$"""{"rules":[{{string.Join(",", rules)}}]}""";

        var summary = Deserialize<RulesSummary>(json);

        Assert.Equal(total, summary.TotalCount);
        Assert.Equal(RulesSummary.RetainedRuleLimit, summary.Rules.Count);
    }
}
