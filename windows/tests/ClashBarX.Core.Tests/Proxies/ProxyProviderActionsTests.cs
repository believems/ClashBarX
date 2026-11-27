using System.Net;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Proxies;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.Proxies;

/// <summary>provider 动作规格测试（源：updateProxyProvider / refreshAllProxyProviders / healthcheck）。</summary>
public class ProxyProviderActionsTests
{
    private static ProxyBoardService CreateService(StubHttpMessageHandler handler) =>
        new(new MihomoApiClient("127.0.0.1:9090", secret: null, handler));

    [Fact]
    public async Task UpdateProviderAsync_PutsProviderEndpoint()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler.RespondJson("{}");
        ProxyBoardService service = CreateService(handler);

        await service.UpdateProviderAsync("alpha");

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Contains("/providers/proxies/alpha", handler.Requests[0].Url);
    }

    [Fact]
    public async Task UpdateAllProvidersAsync_UpdatesEachProviderTolerantly()
    {
        // 列表 + 两次 PUT（第二个失败不阻断——脚本：列表 OK、alpha OK、beta 500）
        using StubHttpMessageHandler handler = StubHttpMessageHandler.Script(
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(
                """{"providers": {"alpha": {"name": "alpha"}, "beta": {"name": "beta"}}}""")),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{}")),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError)));
        ProxyBoardService service = CreateService(handler);

        IReadOnlyList<string> updated = await service.UpdateAllProvidersAsync();

        Assert.Equal(new[] { "alpha" }, updated);
        Assert.Equal(3, handler.Requests.Count);   // 列表 + alpha + beta 都发出
    }

    [Fact]
    public async Task UpdateAllProvidersAsync_EmptyListWhenFetchFails()
    {
        using StubHttpMessageHandler handler = new(
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError()));
        ProxyBoardService service = CreateService(handler);

        IReadOnlyList<string> updated = await service.UpdateAllProvidersAsync();

        Assert.Empty(updated);
    }

    [Fact]
    public async Task HealthcheckProviderAsync_UsesProviderDeclaredParameters()
    {
        // 参数解析 GET（provider 带 testUrl/timeout）→ healthcheck GET 带 url/timeout
        using StubHttpMessageHandler handler = StubHttpMessageHandler.Script(
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json(
                """{"providers": {"alpha": {"name": "alpha", "testUrl": "https://probe.example/204", "timeout": 4000}}}""")),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{}")));
        ProxyBoardService service = CreateService(handler);

        await service.HealthcheckProviderAsync("alpha");

        Assert.Contains("/providers/proxies/alpha", handler.Requests[1].Url);
        Assert.Contains("probe.example", handler.Requests[1].Url);
        Assert.Contains("timeout=4000", handler.Requests[1].Url);
    }

    [Fact]
    public async Task HealthcheckProviderAsync_FallsBackToDefaultsWhenProviderUnknown()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler.Script(
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("""{"providers": {}}""")),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{}")));
        ProxyBoardService service = CreateService(handler);

        await service.HealthcheckProviderAsync("ghost");

        Assert.Contains("/providers/proxies/ghost", handler.Requests[1].Url);
        Assert.Contains("gstatic", handler.Requests[1].Url);   // 缺省测速 URL
        Assert.Contains($"timeout={ProxyBoardService.DefaultHealthcheckTimeoutMilliseconds}", handler.Requests[1].Url);
    }
}

/// <summary>Provider 展示格式化规格（源：Formatters.swift 的相对时间/到期/字节）。</summary>
public class ProviderFormattingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RelativeTime_UsesUnitsPerSource()
    {
        Assert.Equal("未知", ProviderFormatting.RelativeTime(null, Now));
        Assert.Equal("未知", ProviderFormatting.RelativeTime("   ", Now));
        Assert.Equal("未知", ProviderFormatting.RelativeTime("not a date", Now));
        Assert.Equal("未知", ProviderFormatting.RelativeTime("0001-01-01T00:00:00Z", Now));   // mihomo 零值（真机 739946 天前回归）
        Assert.Equal("5 分钟前", ProviderFormatting.RelativeTime(Now.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), Now));
        Assert.Equal("3 小时前", ProviderFormatting.RelativeTime(Now.AddHours(-3).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), Now));
        Assert.Equal("2 天前", ProviderFormatting.RelativeTime(Now.AddDays(-2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), Now));
    }

    [Fact]
    public void DaysUntilExpiryShort_FollowsSourceRules()
    {
        long nowSeconds = Now.ToUnixTimeSeconds();
        Assert.Equal("未知", ProviderFormatting.DaysUntilExpiryShort(null, Now));
        Assert.Equal("长期有效", ProviderFormatting.DaysUntilExpiryShort(0, Now));
        Assert.Equal("未知", ProviderFormatting.DaysUntilExpiryShort(-5, Now));
        Assert.Equal("已过期", ProviderFormatting.DaysUntilExpiryShort(nowSeconds - 864000, Now));
        Assert.Equal("今天到期", ProviderFormatting.DaysUntilExpiryShort(nowSeconds + 3600, Now));
        Assert.Equal("3 天后到期", ProviderFormatting.DaysUntilExpiryShort(nowSeconds + 86400 * 3, Now));
    }

    [Fact]
    public void ExpiresSoon_TrueOnlyWithin15Days()
    {
        long nowSeconds = Now.ToUnixTimeSeconds();
        Assert.False(ProviderFormatting.ExpiresSoon(null, Now));
        Assert.False(ProviderFormatting.ExpiresSoon(0, Now));   // 长期不算
        Assert.False(ProviderFormatting.ExpiresSoon(nowSeconds + 20 * 86400, Now));
        Assert.True(ProviderFormatting.ExpiresSoon(nowSeconds + 14 * 86400, Now));
        Assert.True(ProviderFormatting.ExpiresSoon(nowSeconds + 100, Now));
    }

    [Theory]
    [InlineData(0, "0KB")]
    [InlineData(15 * 1024 * 1024, "15MB")]
    [InlineData(1024 * 1024 * 1024, "1GB")]
    [InlineData((long)(1.5 * 1024 * 1024 * 1024), "1.5GB")]
    public void BytesCompactNoSpace_UnitsAndDecimals(long value, string expected)
    {
        Assert.Equal(expected, ProviderFormatting.BytesCompactNoSpace(value));
    }
}
