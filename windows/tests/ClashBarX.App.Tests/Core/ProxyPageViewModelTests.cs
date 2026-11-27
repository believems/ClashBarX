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

    // ---- 空态提示（内核未运行 → 引导启动；运行中无可显示分组 → 暂无代理分组） ----

    private const string CoreNotRunningHint = "启动内核后此处显示代理分组与订阅源";
    private const string NoGroupsHint = "内核运行中，但当前配置没有可显示的代理分组";

    private static ProxyPageViewModel CreateIdleViewModel() =>
        new(new ProxyBoardService(new StubApiClient()), _ => { }, () => false);

    private static ProxyBoard BoardWithGroups(params string[] groupNames) => ProxyBoard.Empty with
    {
        Groups = groupNames
            .Select(name => new ProxyGroup(name, "Selector", "A", new[] { "A" }, null, null, null, null, Array.Empty<int>()))
            .ToArray(),
    };

    [Fact]
    public void 内核未运行时代理页显示启动引导提示()
    {
        ProxyPageViewModel viewModel = CreateIdleViewModel();

        Assert.Equal(CoreNotRunningHint, viewModel.EmptyStateHint);
        Assert.True(viewModel.HasEmptyStateHint);
    }

    [Fact]
    public void 内核启动后提示消失且面板载入前不闪现无分组提示()
    {
        ProxyPageViewModel viewModel = CreateIdleViewModel();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        viewModel.ApplyCoreRunning(true);

        Assert.Null(viewModel.EmptyStateHint);   // 首帧刷新前（面板未载入）不提示
        Assert.Contains(nameof(ProxyPageViewModel.EmptyStateHint), changed);
        Assert.Contains(nameof(ProxyPageViewModel.HasEmptyStateHint), changed);

        viewModel.ApplyBoard(BoardWithGroups("节点选择"));
        Assert.Null(viewModel.EmptyStateHint);
        Assert.Single(viewModel.Groups);
    }

    [Fact]
    public void 内核运行但无可显示分组时提示暂无代理分组()
    {
        ProxyPageViewModel viewModel = CreateIdleViewModel();
        viewModel.ApplyCoreRunning(true);

        viewModel.ApplyBoard(ProxyBoard.Empty);

        Assert.Equal(NoGroupsHint, viewModel.EmptyStateHint);
        Assert.True(viewModel.HasEmptyStateHint);
    }

    [Fact]
    public void 内核停止清空陈旧分组并回到启动引导提示()
    {
        ProxyPageViewModel viewModel = CreateIdleViewModel();
        viewModel.ApplyCoreRunning(true);
        viewModel.ApplyBoard(BoardWithGroups("节点选择"));
        Assert.Single(viewModel.Groups);

        viewModel.ApplyCoreRunning(false);

        Assert.Empty(viewModel.Groups);   // 源：stopCore 的 clearProxyPresentation
        Assert.Equal(CoreNotRunningHint, viewModel.EmptyStateHint);
    }

    [Fact]
    public void 运行态未变化时空态提示不重复通知()
    {
        ProxyPageViewModel viewModel = CreateIdleViewModel();
        int notifications = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProxyPageViewModel.EmptyStateHint))
            {
                notifications++;
            }
        };

        viewModel.ApplyCoreRunning(false);   // 初始即未运行：幂等

        Assert.Equal(0, notifications);
    }
}
