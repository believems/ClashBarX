using System.Text.Json;
using ClashBarX.Core.Json;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;
using ClashBarX.Core.Proxies;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.Proxies;

/// <summary>
/// 代理面板业务规格测试（源：applyProxyGroupsResponse 排序合并 + 测速流程）：
/// GLOBAL 序分组排序、provider 参数合并、切换、组测速的成员全集与 0 记录、
/// 单节点测速的 provider 预判。
/// </summary>
public class ProxyBoardServiceTests
{
    private static T Deserialize<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, JsonSharedOptions.Json)!;

    private const string ProxiesJson = """
        {
          "proxies": {
            "GLOBAL": { "name": "GLOBAL", "type": "Selector", "now": "香港组", "all": ["香港组", "美国组", "DIRECT"] },
            "香港组": { "name": "香港组", "type": "Selector", "now": "hk-01", "all": ["hk-01", "hk-02"], "history": [{"delay": 80}] },
            "美国组": { "name": "美国组", "type": "URLTest", "now": "us-01", "all": ["us-01"] },
            "DIRECT": { "name": "DIRECT", "type": "Direct", "all": [] },
            "hk-01": { "name": "hk-01", "type": "Shadowsocks", "all": [] },
            "us-01": { "name": "us-01", "type": "Trojan", "all": [] }
          }
        }
        """;

    // provider 集成组：mihomo 把订阅 provider 的节点暴露为同名代理组（名字=provider 名），
    // 其 testUrl/timeout 由 provider 详情提供（源：providerLookup[proxy.name] 的键语义）
    private const string ProvidersJson = """
        {
          "providers": {
            "美国组": {
              "name": "美国组", "vehicleType": "HTTP",
              "testUrl": "https://provider.example/204", "timeout": 6000,
              "proxies": [ { "name": "hk-01", "type": "Shadowsocks", "history": [{"delay": 90}] } ]
            }
          }
        }
        """;

    [Fact]
    public void 分组按GLOBAL序排序_其余按名称()
    {
        ProxyGroupsResponse response = Deserialize<ProxyGroupsResponse>(ProxiesJson);
        ProviderSummary? providers = Deserialize<ProviderSummary>(ProvidersJson);

        ProxyBoard board = ProxyBoardService.BuildBoard(response, providers);

        // GLOBAL.all 序：香港组、美国组（DIRECT 不是分组，all 为空被过滤）
        Assert.Equal(new[] { "香港组", "美国组", "GLOBAL" }, board.Groups.Select(group => group.Name));
    }

    [Fact]
    public void 合并provider的测速参数与节点延迟()
    {
        ProxyGroupsResponse response = Deserialize<ProxyGroupsResponse>(ProxiesJson);
        ProviderSummary providers = Deserialize<ProviderSummary>(ProvidersJson);

        ProxyBoard board = ProxyBoardService.BuildBoard(response, providers);

        // 美国组自身无 testUrl → 合并同名 provider 的；香港组无对应 provider 保持 null
        ProxyGroup america = board.Groups.First(group => group.Name == "美国组");
        Assert.Equal("https://provider.example/204", america.TestUrl);
        Assert.Equal(6000, america.Timeout);
        ProxyGroup hongKong = board.Groups.First(group => group.Name == "香港组");
        Assert.Null(hongKong.TestUrl);

        // 节点类型与延迟历史：hk-01 自身无 history → 取 provider 的 90；香港组自身 history 为 80
        Assert.Equal("Shadowsocks", board.NodeTypes["hk-01"]);
        Assert.Equal(new[] { 90 }, board.DelaySamples["hk-01"]);
        Assert.Equal(new[] { 80 }, board.DelaySamples["香港组"]);
        Assert.Equal("Trojan", board.NodeTypes["us-01"]);
    }

    [Fact]
    public async Task 刷新容错provider不可用()
    {
        var handler = new StubHttpMessageHandler(
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(ProxiesJson)),
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError()));
        using var client = new MihomoApiClient("127.0.0.1:9090", null, handler);
        // providers 请求失败：GET 重试 3 次都失败（3 步错误）→ 仍应拿到分组
        handler.RepeatLastStep = false;
        var client2 = new MihomoApiClient("127.0.0.1:9090", null, handler);
        using var pooled = client2;
        var service = new ProxyBoardService(client2);

        ProxyBoard board = await service.RefreshAsync();

        Assert.Equal(3, board.Groups.Count);
    }

    [Fact]
    public async Task 切换节点发PUT()
    {
        var handler = new StubHttpMessageHandler(
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(ProxiesJson)),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(ProvidersJson)),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{}")));
        using var client = new MihomoApiClient("127.0.0.1:9090", null, handler);
        var service = new ProxyBoardService(client);

        await service.SwitchNodeAsync("香港组", "hk-02");

        RecordedHttpRequest request = handler.Requests[^1];
        Assert.Equal("PUT", request.Method.Method);
        Assert.Equal("http://127.0.0.1:9090/proxies/%E9%A6%99%E6%B8%AF%E7%BB%84", request.Url);
        Assert.Equal("{\"name\":\"hk-02\"}", System.Text.Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public async Task 组测速返回成员全集_未测到的记0()
    {
        var handler = new StubHttpMessageHandler(
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(ProxiesJson)),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(ProvidersJson)),
            // 测速只回 hk-01；hk-02 缺失应记 0
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{\"hk-01\": 120}")));
        using var client = new MihomoApiClient("127.0.0.1:9090", null, handler);
        var service = new ProxyBoardService(client);

        IReadOnlyDictionary<string, int> result = await service.TestGroupAsync("香港组");

        Assert.Equal(2, result.Count);
        Assert.Equal(120, result["hk-01"]);
        Assert.Equal(0, result["hk-02"]);
    }

    [Fact]
    public async Task 组测速全员失败记全0()
    {
        var handler = new StubHttpMessageHandler(
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(ProxiesJson)),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(ProvidersJson)),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Text("An error occurred", System.Net.HttpStatusCode.ServiceUnavailable)));
        using var client = new MihomoApiClient("127.0.0.1:9090", null, handler);
        var service = new ProxyBoardService(client);

        IReadOnlyDictionary<string, int> result = await service.TestGroupAsync("香港组");

        Assert.Equal(2, result.Count);
        Assert.All(result.Values, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task provider节点测速走provider端点()
    {
        var handler = new StubHttpMessageHandler(
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(ProxiesJson)),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(ProvidersJson)),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{\"delay\": 95}")));
        using var client = new MihomoApiClient("127.0.0.1:9090", null, handler);
        var service = new ProxyBoardService(client);

        // hk-01 属于「美国组」provider → provider 单节点 healthcheck 端点
        int? delay = await service.TestNodeAsync("香港组", "hk-01");

        Assert.Equal(95, delay);
        Assert.Equal(
            "http://127.0.0.1:9090/providers/proxies/%E7%BE%8E%E5%9B%BD%E7%BB%84/hk-01/healthcheck",
            handler.Requests[^1].Url.Split('?')[0]);
    }

    [Fact]
    public async Task 非provider节点测速走delay端点()
    {
        var handler = new StubHttpMessageHandler(
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(ProxiesJson)),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(ProvidersJson)),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{\"delay\": 210}")));
        using var client = new MihomoApiClient("127.0.0.1:9090", null, handler);
        var service = new ProxyBoardService(client);

        int? delay = await service.TestNodeAsync("香港组", "hk-02");   // 不在 provider 名单

        Assert.Equal(210, delay);
        Assert.Equal(
            "http://127.0.0.1:9090/proxies/hk-02/delay",
            handler.Requests[^1].Url.Split('?')[0]);
    }

    // ---- 真订阅源判定（源：macOS shouldIncludeProxyProvider；mihomo 把内置 default 与每个分组
    //      都作为 vehicleType=Compatible 的伪 provider 回传，真机实测 BOSS 配置回 13 条仅 1 条真） ----

    [Theory]
    [InlineData("default", "HTTP", false)]          // 内置 default 一律不展示
    [InlineData("Default", "Compatible", false)]    // 大小写不敏感
    [InlineData("GOOGLE", "Compatible", false)]     // 分组伪 provider
    [InlineData("jf_link", "HTTP", true)]
    [InlineData("local", "File", true)]
    [InlineData("x", null, true)]                   // 无 vehicleType 不按 Compatible 排除
    public void 真订阅源判定按Mac规格(string key, string? vehicleType, bool expected)
    {
        var detail = new ProviderDetail(
            Name: key, VehicleType: vehicleType, TestUrl: null, Timeout: null,
            UpdatedAt: null, RuleCount: null, SubscriptionInfo: null, Proxies: null);

        Assert.Equal(expected, ProxyBoardService.ShouldIncludeProxyProvider(key, detail));
    }

    [Fact]
    public void 名称回退键名判定default()
    {
        var detail = new ProviderDetail(
            Name: "  ", VehicleType: "HTTP", TestUrl: null, Timeout: null,
            UpdatedAt: null, RuleCount: null, SubscriptionInfo: null, Proxies: null);

        Assert.False(ProxyBoardService.ShouldIncludeProxyProvider("default", detail));
    }
}
