using System.IO;
using System.Net;
using System.Text;
using ClashBarX.Core.Subscriptions;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.Subscriptions;

/// <summary>
/// 订阅导入/管理规格测试（源：ConfigService.swift 的 ConfigImportService）：
/// 下载（UA/超限）、Clash 配置校验、{host}.yaml 命名、原子写、记录与选择持久化。
/// </summary>
public class SubscriptionServiceTests : IDisposable
{
    private readonly string _root;

    public SubscriptionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "clashbarx-sub-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录残留无害
        }
    }

    private SubscriptionService CreateService(HttpMessageHandler? handler = null) =>
        new(
            Path.Combine(_root, "configs"),
            Path.Combine(_root, "subscriptions.json"),
            handler);

    private const string ValidYaml = "proxies:\n  - name: node-a\n    type: ss\nproxy-groups:\n  - name: g\n";

    private static StubHttpMessageHandler RespondYaml(string yaml, Action<RecordedHttpRequest>? onRequest = null)
    {
        var handler = new StubHttpMessageHandler(new StubHttpMessageHandler.ScriptStep(
            Handler: (request, ct) =>
            {
                onRequest?.Invoke(new RecordedHttpRequest(
                    request.Method,
                    request.RequestUri!.AbsoluteUri,
                    request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
                    null));
                return Task.FromResult(StubHttpMessageHandler.Text(yaml, HttpStatusCode.OK));
            }));
        handler.RepeatLastStep = true;   // 多次下载（重复导入）场景复用同一步
        return handler;
    }

    private sealed record RecordedHttpRequest(
        HttpMethod Method,
        string Url,
        IReadOnlyDictionary<string, string> Headers,
        byte[]? Body);

    [Fact]
    public async Task 导入订阅下载落盘并记录()
    {
        string? capturedUa = null;
        using StubHttpMessageHandler handler = RespondYaml(ValidYaml, request =>
            capturedUa = request.Headers.TryGetValue("User-Agent", out string? ua) ? ua : null);
        SubscriptionService service = CreateService(handler);

        SubscriptionInfo info = await service.AddAsync(new Uri("https://sub.example.com/config.yaml"), "clash.meta/v1.19.34");

        Assert.Equal("clash.meta/v1.19.34", capturedUa);
        Assert.Equal("sub.example.com.yaml", info.FileName);
        Assert.True(File.Exists(Path.Combine(_root, "configs", "sub.example.com.yaml")));
        Assert.Equal(ValidYaml, File.ReadAllText(Path.Combine(_root, "configs", "sub.example.com.yaml")));
        Assert.Single(service.List());
        Assert.Equal("https://sub.example.com/config.yaml", service.List()[0].Url);
    }

    [Fact]
    public async Task 文件名取host不含端口()
    {
        // 与 Swift url.host 同语义：host 不含端口，端口不进文件名
        Assert.Equal("sub.example.com.yaml", SubscriptionService.InferRemoteConfigFileName(new Uri("https://sub.example.com/a/b.yaml")));
        Assert.Equal("example.com.yaml", SubscriptionService.InferRemoteConfigFileName(new Uri("http://example.com:8080/clash")));
        // 冒号替换针对 IPv6 字面量 host（源：safeHost 的 ":" 替换）
        Assert.Equal("--1.yaml", SubscriptionService.InferRemoteConfigFileName(new Uri("http://[::1]:8080/clash")));
    }

    [Fact]
    public async Task 重复导入同一订阅更新记录不新增()
    {
        using StubHttpMessageHandler handler = RespondYaml(ValidYaml);
        SubscriptionService service = CreateService(handler);
        Uri url = new("https://sub.example.com/config.yaml");

        await service.AddAsync(url);
        SubscriptionInfo second = await service.AddAsync(url);

        Assert.Single(service.List());
        Assert.Equal(second.LastSuccessAt, service.List()[0].LastSuccessAt);
    }

    [Fact]
    public async Task 非Clash配置被拒绝且不落盘()
    {
        StubHttpMessageHandler handler = new(new StubHttpMessageHandler.ScriptStep(
            Response: StubHttpMessageHandler.Text("<html>not a config</html>", HttpStatusCode.OK)));
        SubscriptionService service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<SubscriptionException>(
            () => service.AddAsync(new Uri("https://sub.example.com/config.yaml")));

        Assert.Contains("proxies", exception.Message);
        Assert.Empty(service.List());
        Assert.False(File.Exists(Path.Combine(_root, "configs", "sub.example.com.yaml")));
    }

    [Fact]
    public async Task 空响应被拒绝()
    {
        StubHttpMessageHandler handler = new(new StubHttpMessageHandler.ScriptStep(
            Response: StubHttpMessageHandler.Text(string.Empty, HttpStatusCode.OK)));
        SubscriptionService service = CreateService(handler);

        await Assert.ThrowsAsync<SubscriptionException>(
            () => service.AddAsync(new Uri("https://sub.example.com/config.yaml")));
    }

    [Fact]
    public async Task 非成功状态码被拒绝()
    {
        StubHttpMessageHandler handler = new(new StubHttpMessageHandler.ScriptStep(
            Response: StubHttpMessageHandler.Text("forbidden", HttpStatusCode.Forbidden)));
        SubscriptionService service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<SubscriptionException>(
            () => service.AddAsync(new Uri("https://sub.example.com/config.yaml")));
        Assert.Contains("403", exception.Message);
    }

    [Fact]
    public async Task 内容超出上限被拒绝()
    {
        // 声明 Content-Length 超限：预检分支
        var response = StubHttpMessageHandler.Text(new string('x', SubscriptionService.MaxRemoteConfigBytes + 1), HttpStatusCode.OK);
        StubHttpMessageHandler handler = new(new StubHttpMessageHandler.ScriptStep(Response: response));
        SubscriptionService service = CreateService(handler);

        await Assert.ThrowsAsync<SubscriptionException>(
            () => service.AddAsync(new Uri("https://sub.example.com/config.yaml")));
    }

    [Fact]
    public void 移除订阅删除文件与记录并清选择()
    {
        using StubHttpMessageHandler handler = RespondYaml(ValidYaml);
        SubscriptionService service = CreateService(handler);
        service.AddAsync(new Uri("https://sub.example.com/config.yaml")).GetAwaiter().GetResult();
        service.Select("sub.example.com.yaml");
        Assert.NotNull(service.GetSelectedFileName());

        service.Remove("sub.example.com.yaml");

        Assert.Empty(service.List());
        Assert.Null(service.GetSelectedFileName());
        Assert.False(File.Exists(Path.Combine(_root, "configs", "sub.example.com.yaml")));
    }

    [Fact]
    public void 选择不存在的配置被拒绝()
    {
        SubscriptionService service = CreateService();

        Assert.Throws<SubscriptionException>(() => service.Select("absent.yaml"));
    }

    [Fact]
    public async Task 选择与配置路径()
    {
        using StubHttpMessageHandler handler = RespondYaml(ValidYaml);
        SubscriptionService service = CreateService(handler);
        await service.AddAsync(new Uri("https://sub.example.com/config.yaml"));

        Assert.Null(service.GetSelectedConfigPath());

        service.Select("sub.example.com.yaml");
        Assert.Equal(
            Path.Combine(_root, "configs", "sub.example.com.yaml"),
            service.GetSelectedConfigPath());
    }

    [Fact]
    public async Task 记录在选择间持久化()
    {
        using StubHttpMessageHandler handler = RespondYaml(ValidYaml);
        SubscriptionService first = CreateService(handler);
        await first.AddAsync(new Uri("https://sub.example.com/config.yaml"));
        first.Select("sub.example.com.yaml");

        // 新实例（新会话）读取同一存储
        SubscriptionService second = CreateService(handler);
        Assert.Single(second.List());
        Assert.Equal("sub.example.com.yaml", second.GetSelectedFileName());
    }

    [Fact]
    public void 配置文件清单只含yaml扩展并排序()
    {
        string configs = Path.Combine(_root, "configs");
        Directory.CreateDirectory(configs);
        File.WriteAllText(Path.Combine(configs, "b.yaml"), ValidYaml);
        File.WriteAllText(Path.Combine(configs, "a.yml"), ValidYaml);
        File.WriteAllText(Path.Combine(configs, "ignore.txt"), "x");

        SubscriptionService service = CreateService();

        Assert.Equal(new[] { "a.yml", "b.yaml" }, service.ListConfigFiles());
    }

    [Fact]
    public async Task 同内容重复导入跳过写入()
    {
        using StubHttpMessageHandler handler = RespondYaml(ValidYaml);
        SubscriptionService service = CreateService(handler);
        string target = Path.Combine(_root, "configs", "sub.example.com.yaml");

        await service.AddAsync(new Uri("https://sub.example.com/config.yaml"));
        string contentBefore = File.ReadAllText(target);
        await service.AddAsync(new Uri("https://sub.example.com/config.yaml"));

        Assert.Equal(contentBefore, File.ReadAllText(target));   // 内容稳定
        Assert.Single(service.List());                             // 记录仍单条
    }
}
