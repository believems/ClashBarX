using System.Net;
using ClashBarX.Core.Maintenance;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.Maintenance;

/// <summary>
/// 维护动作规格测试。GEO 与「已是最新版本」两处的响应形态取自真机实测（mihomo v1.19.34）：
/// /upgrade/geo 回 204 无响应体；/upgrade 在已是最新时回 500 + JSON。
/// </summary>
public class MaintenanceServiceTests
{
    private const string Controller = "http://127.0.0.1:9090";

    private const string AlreadyLatestBody = """{"message":"update error: already using latest version v1.19.34"}""";

    private static HttpResponseMessage NoContent() =>
        new(HttpStatusCode.NoContent) { Content = new StringContent(string.Empty) };

    [Fact]
    public async Task UpgradeGeo_内核回204空响应体时视为成功()
    {
        using StubHttpMessageHandler handler = new(new StubHttpMessageHandler.ScriptStep(Response: NoContent()));
        using var client = new MihomoApiClient(Controller, null, handler);

        await new MaintenanceService(client).UpgradeGeoAsync();   // 不抛即成功（修复前：对空响应体做 JSON 解析必抛）

        RecordedHttpRequest request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/upgrade/geo", request.Url);
    }

    [Fact]
    public async Task UpgradeGeo_非2xx仍抛异常交由调用方反馈()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler
            .RespondJson("""{"message":"geo update failed"}""", HttpStatusCode.InternalServerError);
        using var client = new MihomoApiClient(Controller, null, handler);

        MihomoApiException error = await Assert.ThrowsAsync<MihomoApiException>(
            () => new MaintenanceService(client).UpgradeGeoAsync());

        Assert.Equal(500, error.StatusCode);
    }

    [Fact]
    public async Task UpgradeCore_已是最新版本时归一为AlreadyLatest并带版本号()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler
            .RespondJson(AlreadyLatestBody, HttpStatusCode.InternalServerError);
        using var client = new MihomoApiClient(Controller, null, handler);

        CoreUpgradeResult result = await new MaintenanceService(client).UpgradeCoreWithResultAsync();

        Assert.Equal(CoreUpgradeOutcome.AlreadyLatest, result.Outcome);
        Assert.Equal("1.19.34", result.Version);
    }

    [Fact]
    public async Task UpgradeCore_2xx且status为ok时归一为Succeeded()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler
            .RespondJson("""{"status":"ok","message":"downloaded"}""");
        using var client = new MihomoApiClient(Controller, null, handler);

        CoreUpgradeResult result = await new MaintenanceService(client).UpgradeCoreWithResultAsync();

        Assert.Equal(CoreUpgradeOutcome.Succeeded, result.Outcome);
        Assert.Equal("ok", result.Status);
        Assert.Equal("downloaded", result.Message);
    }

    [Fact]
    public async Task UpgradeCore_真失败时归一为Failed并保留原因()
    {
        using StubHttpMessageHandler handler = new(new StubHttpMessageHandler.ScriptStep(
            Response: StubHttpMessageHandler.Text("download failed: connection reset", HttpStatusCode.InternalServerError)));
        using var client = new MihomoApiClient(Controller, null, handler);

        CoreUpgradeResult result = await new MaintenanceService(client).UpgradeCoreWithResultAsync();

        Assert.Equal(CoreUpgradeOutcome.Failed, result.Outcome);
        Assert.Equal("download failed: connection reset", result.Message);
    }

    [Theory]
    [InlineData("update error: already using latest version v1.19.34", CoreUpgradeOutcome.AlreadyLatest, "1.19.34")]
    [InlineData("Already Using Latest Version v1.2.3", CoreUpgradeOutcome.AlreadyLatest, "1.2.3")]      // 忽略大小写
    [InlineData("already using latest version", CoreUpgradeOutcome.AlreadyLatest, null)]                // 无版本号
    [InlineData("download failed", CoreUpgradeOutcome.Failed, null)]
    [InlineData("", CoreUpgradeOutcome.Failed, null)]
    [InlineData(null, CoreUpgradeOutcome.Failed, null)]
    public void FromMessage_按macOS口径归一(string? message, CoreUpgradeOutcome expectedOutcome, string? expectedVersion)
    {
        CoreUpgradeResult result = CoreUpgradeResult.FromMessage(message);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedVersion, result.Version);
    }

    [Fact]
    public void FromError_非HTTP错误按异常文案归一为失败()
    {
        CoreUpgradeResult result = CoreUpgradeResult.FromError(new TimeoutException("mihomo request timed out"));

        Assert.Equal(CoreUpgradeOutcome.Failed, result.Outcome);
        Assert.Equal("mihomo request timed out", result.Message);
    }

    [Fact]
    public void FromError_错误体是无message的JSON时按原文归一()
    {
        var error = MihomoApiException.HttpError(500, """{"message":"update error: already using latest version v1.19.34"}""");

        CoreUpgradeResult result = CoreUpgradeResult.FromError(error);

        Assert.Equal(CoreUpgradeOutcome.AlreadyLatest, result.Outcome);
        Assert.Equal("1.19.34", result.Version);
    }
}
