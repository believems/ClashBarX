using System.Net;
using System.Text;
using System.Text.Json;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Models;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.Mihomo;

public class MihomoApiClientTests : IDisposable
{
    private const string Controller = "127.0.0.1:9090";

    private readonly MihomoApiClient _client = new(Controller, "s3cret");

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task 每个端点发出的请求行与认证头都符合规格()
    {
        foreach (var row in MihomoEndpointTableTests.EndpointSpecs)
        {
            var factory = (Func<MihomoEndpoint>)row[1]!;
            MihomoEndpoint endpoint = factory();

            var handler = StubHttpMessageHandler.RespondJson("{}");
            using var client = new MihomoApiClient(Controller, "s3cret", handler);
            await client.RequestNoResponseAsync(endpoint);

            Assert.Single(handler.Requests);
            RecordedHttpRequest recorded = handler.Requests[0];

            // 期望 URL 用 BCL 原生转义构造（外部基准，不经过被测代码）
            string expectedQuery = endpoint.QueryItems.Count == 0
                ? string.Empty
                : "?" + string.Join("&", endpoint.QueryItems.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            string expectedUrl = $"http://{Controller}{endpoint.Path}{expectedQuery}";

            Assert.Equal(endpoint.Method.ToHttpMethod(), recorded.Method);
            Assert.Equal(expectedUrl, recorded.Url);
            Assert.Equal("Bearer s3cret", recorded.Headers["Authorization"]);
            Assert.Equal("application/json", recorded.Headers["Content-Type"]);
            Assert.True(
                (endpoint.Body == null && recorded.Body!.Length == 0)
                || (endpoint.Body != null && recorded.Body!.AsSpan().SequenceEqual(endpoint.Body)),
                $"端点 {endpoint.Kind} 请求体与规格不符");
        }
    }

    [Fact]
    public async Task 无密钥时不发送Authorization头()
    {
        var handler = StubHttpMessageHandler.RespondJson("{\"version\":\"1.18.8\"}");
        using var client = new MihomoApiClient(Controller, secret: null, handler);
        await client.RequestNoResponseAsync(MihomoEndpoint.Version());

        Assert.False(handler.Requests[0].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task 空密钥同样不发送Authorization头()
    {
        var handler = StubHttpMessageHandler.RespondJson("{}");
        using var client = new MihomoApiClient(Controller, secret: "", handler);
        await client.RequestNoResponseAsync(MihomoEndpoint.Version());

        Assert.False(handler.Requests[0].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task 运行时更新凭据后立即生效()
    {
        var handler = StubHttpMessageHandler.RespondJson("{}");
        using var client = new MihomoApiClient(Controller, "old", handler);
        client.UpdateCredentials("192.168.1.10:9091", "new-secret");

        await client.RequestNoResponseAsync(MihomoEndpoint.Version());

        Assert.Equal("http://192.168.1.10:9091/version", handler.Requests[0].Url);
        Assert.Equal("Bearer new-secret", handler.Requests[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task 非成功状态码不重试并携带响应体文本()
    {
        var handler = StubHttpMessageHandler.Script(new StubHttpMessageHandler.ScriptStep(
            Response: StubHttpMessageHandler.Text("boom message", HttpStatusCode.InternalServerError)));

        var exception = await Assert.ThrowsAsync<MihomoApiException>(
            () => _clientWith(handler).RequestNoResponseAsync(MihomoEndpoint.Version()));

        Assert.Equal(500, exception.StatusCode);
        Assert.Equal("boom message", exception.ResponseBody);
        Assert.Contains("API request failed (500): boom message", exception.Message);
        Assert.Single(handler.Requests);   // 不重试
    }

    [Fact]
    public async Task GET传输失败最多重试三次()
    {
        var handler = StubHttpMessageHandler.Script(
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError()),
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError()),
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError()));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => _clientWith(handler).RequestNoResponseAsync(MihomoEndpoint.Version()));

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task GET传输失败后重试成功()
    {
        var handler = StubHttpMessageHandler.Script(
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError()),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{\"version\":\"ok\"}")));

        var version = await _clientWith(handler).RequestAsync<VersionInfo>(MihomoEndpoint.Version());

        Assert.Equal("ok", version.Version);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task 非GET方法传输失败不重试()
    {
        var handler = StubHttpMessageHandler.Script(
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.ConnectionError()));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => _clientWith(handler).RequestNoResponseAsync(MihomoEndpoint.SwitchProxy("g", "n")));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 短超时端点超时后只重试一次()
    {
        // version 端点超时 2s（< 5s）：仅 attempt==0 时重试，合计 2 次
        var handler = StubHttpMessageHandler.Script(
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.SimulatedTimeout()),
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.SimulatedTimeout()));

        await Assert.ThrowsAsync<TimeoutException>(
            () => _clientWith(handler).RequestNoResponseAsync(MihomoEndpoint.Version()));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task 长超时端点超时后不重试()
    {
        // getConfigs 端点超时 5s（>= 5s）：超时即失败
        var handler = StubHttpMessageHandler.Script(
            new StubHttpMessageHandler.ScriptStep(Error: StubHttpMessageHandler.SimulatedTimeout()));

        await Assert.ThrowsAsync<TimeoutException>(
            () => _clientWith(handler).RequestNoResponseAsync(MihomoEndpoint.GetConfigs()));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 超时端点的请求取消真实生效()
    {
        // 挂起请求直至内部 CTS 超时（version=2s），第二次成功：验证 CancelAfter 链路真实工作
        var handler = StubHttpMessageHandler.Script(
            new StubHttpMessageHandler.ScriptStep(Handler: (_, ct) => WaitForeverAsync(ct)),
            new StubHttpMessageHandler.ScriptStep(Response: StubHttpMessageHandler.Json("{}")));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await _clientWith(handler).RequestNoResponseAsync(MihomoEndpoint.Version());
        watch.Stop();

        Assert.Equal(2, handler.Requests.Count);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.9), $"真实等待时间 {watch.Elapsed} 过短");
    }

    [Fact]
    public async Task 外部取消立即传播且不重试()
    {
        var handler = StubHttpMessageHandler.Script(
            new StubHttpMessageHandler.ScriptStep(Handler: (_, ct) => WaitForeverAsync(ct)));

        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _clientWith(handler).RequestNoResponseAsync(MihomoEndpoint.Version(), cts.Token));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task 成功状态但非合法JSON时抛解码异常()
    {
        var handler = StubHttpMessageHandler.RespondJson("not json at all");

        await Assert.ThrowsAsync<JsonException>(
            () => _clientWith(handler).RequestAsync<VersionInfo>(MihomoEndpoint.Version()));
    }

    [Fact]
    public async Task 成功状态但响应为null字面量时抛无效响应()
    {
        var handler = StubHttpMessageHandler.RespondJson("null");

        await Assert.ThrowsAsync<MihomoApiException>(
            () => _clientWith(handler).RequestAsync<VersionInfo>(MihomoEndpoint.Version()));
    }

    [Fact]
    public async Task 请求解码为模型()
    {
        var handler = StubHttpMessageHandler.RespondJson("{\"version\":\"1.18.8\"}");
        var version = await _clientWith(handler).RequestAsync<VersionInfo>(MihomoEndpoint.Version());

        Assert.Equal("1.18.8", version.Version);
    }

    [Fact]
    public void 构建WebSocket请求_url与认证头符合规格()
    {
        var request = _client.BuildWebSocketRequest(MihomoEndpoint.Traffic());
        Assert.Equal("ws://127.0.0.1:9090/traffic", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer s3cret", request.Headers["Authorization"]);

        request = _client.BuildWebSocketRequest(MihomoEndpoint.Connections(1000));
        Assert.Equal("ws://127.0.0.1:9090/connections?interval=1000", request.Uri.AbsoluteUri);

        request = _client.BuildWebSocketRequest(MihomoEndpoint.Logs("info"));
        Assert.Equal("ws://127.0.0.1:9090/logs?level=info", request.Uri.AbsoluteUri);
    }

    [Fact]
    public void 无密钥的WebSocket请求不带认证头()
    {
        using var client = new MihomoApiClient(Controller, null);
        var request = client.BuildWebSocketRequest(MihomoEndpoint.Memory());
        Assert.Empty(request.Headers);
    }

    [Fact]
    public void 非法控制器地址抛无效URL()
    {
        using var client = new MihomoApiClient("   ", "s");
        Assert.Throws<MihomoApiException>(() => client.BuildWebSocketRequest(MihomoEndpoint.Traffic()));
    }

    // ---- 辅助 ----

    private static async Task<HttpResponseMessage> WaitForeverAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("不可达");
    }

    private MihomoApiClient _clientWith(StubHttpMessageHandler handler) => new(Controller, "s3cret", handler);
}
