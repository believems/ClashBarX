using ClashBarX.App.Core;
using ClashBarX.Core.Models;
using ClashBarX.Core.Proxies;

namespace ClashBarX.App.Tests.Core;

/// <summary>代理页模型规格测试（mode 切换/流量摘要/分组卡）。</summary>
public class ProxyPageViewModelTests
{
    private sealed class Harness
    {
        public List<string> Modes = new();
        public List<string> Logs = new();
        public ProxyPageViewModel ViewModel = null!;

        public Harness()
        {
            ViewModel = new ProxyPageViewModel(
                new ProxyBoardService(new StubApiClient()),
                message => Logs.Add(message),
                () => true);
            ViewModel.ModeChanged += Modes.Add;
        }
    }

    private sealed class StubApiClient : ClashBarX.Core.Mihomo.IMihomoApiClient
    {
        public void UpdateCredentials(string controller, string? secret) { }

        public Task<T> RequestAsync<T>(ClashBarX.Core.Mihomo.MihomoEndpoint endpoint,
            CancellationToken cancellationToken = default)
            where T : class => Task.FromResult((T)(object)new ProxyGroupsResponse(
            new Dictionary<string, ProxyGroup>()));

        public Task RequestNoResponseAsync(ClashBarX.Core.Mihomo.MihomoEndpoint endpoint,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ClashBarX.Core.Mihomo.IWebSocketConnection CreateWebSocketConnection(
            ClashBarX.Core.Mihomo.MihomoEndpoint endpoint) =>
            throw new NotSupportedException();

        public void Dispose() { }
    }

    [Fact]
    public void 模式切换上抛事件()
    {
        var harness = new Harness();

        harness.ViewModel.Mode = "global";
        harness.ViewModel.Mode = "direct";

        Assert.Equal(new[] { "global", "direct" }, harness.Modes);
    }

    [Fact]
    public void 三态布尔与模式联动()
    {
        var harness = new Harness();

        harness.ViewModel.IsModeGlobal = true;
        Assert.Equal("global", harness.ViewModel.Mode);
        Assert.True(harness.ViewModel.IsModeGlobal);
        Assert.False(harness.ViewModel.IsModeRule);

        harness.ViewModel.IsModeDirect = true;
        Assert.Equal("direct", harness.ViewModel.Mode);

        // 取消勾选不改变模式（radio 语义：只有选中生效）
        harness.ViewModel.IsModeRule = false;
        Assert.Equal("direct", harness.ViewModel.Mode);
    }

    [Fact]
    public void ApplyMode初始化不触发事件()
    {
        var harness = new Harness();

        harness.ViewModel.ApplyMode("global");

        Assert.Equal("global", harness.ViewModel.Mode);
        Assert.True(harness.ViewModel.IsModeGlobal);
        Assert.Empty(harness.Modes);
    }

    [Fact]
    public void ApplyMode空值回落rule()
    {
        var harness = new Harness();

        harness.ViewModel.ApplyMode("");

        Assert.Equal("rule", harness.ViewModel.Mode);
        Assert.True(harness.ViewModel.IsModeRule);
    }

    [Fact]
    public void 流量摘要格式化()
    {
        var harness = new Harness();

        harness.ViewModel.UpdateTraffic(1024, 2048);

        Assert.Contains("1.0 KB/s", harness.ViewModel.TrafficSummary);
        Assert.Contains("2.0 KB/s", harness.ViewModel.TrafficSummary);
    }

    [Fact]
    public void 流量概览开关属性()
    {
        var harness = new Harness();

        Assert.True(harness.ViewModel.ShowTrafficOverview);
        harness.ViewModel.ShowTrafficOverview = false;
        Assert.False(harness.ViewModel.ShowTrafficOverview);
    }
}
