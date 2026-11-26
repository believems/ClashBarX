using ClashBarX.App.Core;
using ClashBarX.Core.Models;
using ClashBarX.Core.Proxies;

namespace ClashBarX.App.Tests.Core;

/// <summary>分组卡四视图规格测试（源：ProxyGroupsViewModel/ProxyProvidersAndGroupsView——
/// hidden 过滤、GLOBAL 按模式、延迟排序过滤、信号条、测全部）。</summary>
public class ProxyGroupViewOptionsTests
{
    private sealed class Harness
    {
        public List<string> Logs = new();
        public List<string> TestAllRequests = new();
        public ProxyPageViewModel ViewModel = null!;

        public Harness()
        {
            ViewModel = new ProxyPageViewModel(
                new ProxyBoardService(new StubApiClient()),
                message => Logs.Add(message),
                () => true);
            ViewModel.TestAllGroupsRequested += () => TestAllRequests.Add("fired");
        }
    }

    private sealed class StubApiClient : ClashBarX.Core.Mihomo.IMihomoApiClient
    {
        public void UpdateCredentials(string controller, string? secret) { }

        public Task<T> RequestAsync<T>(ClashBarX.Core.Mihomo.MihomoEndpoint endpoint,
            CancellationToken cancellationToken = default)
            where T : class => throw new NotSupportedException();

        public Task RequestNoResponseAsync(ClashBarX.Core.Mihomo.MihomoEndpoint endpoint,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ClashBarX.Core.Mihomo.IWebSocketConnection CreateWebSocketConnection(
            ClashBarX.Core.Mihomo.MihomoEndpoint endpoint) =>
            throw new NotSupportedException();

        public void Dispose() { }
    }

    private static ProxyBoard Board(
        IEnumerable<ProxyGroup> groups,
        IReadOnlyDictionary<string, IReadOnlyList<int>>? delays = null,
        IReadOnlyDictionary<string, string>? nodeTypes = null) => new(
        groups.ToList(),
        nodeTypes ?? new Dictionary<string, string>(),
        delays ?? new Dictionary<string, IReadOnlyList<int>>(),
        new Dictionary<string, ProviderDetail>());

    private static ProxyGroup Group(
        string name,
        string[] all,
        bool hidden = false,
        string? icon = null) => new(
        name, "Selector", all.Length > 0 ? all[0] : null, all, null, null, icon, hidden, Array.Empty<int>());

    [Fact]
    public void HiddenGroups_FilteredByDefault_ShownWhenToggled()
    {
        var harness = new Harness();
        harness.ViewModel.ApplyBoard(Board(new[]
        {
            Group("可见组", new[] { "a" }),
            Group("隐藏组", new[] { "b" }, hidden: true),
        }));

        Assert.Equal(new[] { "可见组" }, harness.ViewModel.Groups.Select(group => group.Name));

        harness.ViewModel.ShowHiddenProxyGroups = true;
        Assert.Equal(2, harness.ViewModel.Groups.Count);

        harness.ViewModel.ShowHiddenProxyGroups = false;
        Assert.Single(harness.ViewModel.Groups);
    }

    [Fact]
    public void GlobalGroup_HiddenOutsideGlobalMode()
    {
        // 源：updateFilteredProxyGroups——非 global 模式滤 GLOBAL
        var harness = new Harness();
        harness.ViewModel.ApplyMode("rule");
        harness.ViewModel.ApplyBoard(Board(new[]
        {
            Group("GLOBAL", new[] { "a" }),
            Group("规则组", new[] { "b" }),
        }));
        Assert.Equal(new[] { "规则组" }, harness.ViewModel.Groups.Select(group => group.Name));

        harness.ViewModel.ApplyMode("global");
        harness.ViewModel.ApplyBoard(Board(new[]
        {
            Group("GLOBAL", new[] { "a" }),
            Group("规则组", new[] { "b" }),
        }));
        // global 模式下 GLOBAL 可见；顺序照 board 输入序（源：filteredProxyGroups 只过滤不重排）
        Assert.Equal(new[] { "GLOBAL", "规则组" }, harness.ViewModel.Groups.Select(group => group.Name));
    }

    [Fact]
    public void LatencySort_OrdersAvailableAscendingAndFiltersUnavailable()
    {
        // 源：sortedNodes——可用按延迟升序、未测/超时过滤（排序模式）
        var harness = new Harness();
        var delays = new Dictionary<string, IReadOnlyList<int>>
        {
            ["fast"] = new[] { 80 },
            ["slow"] = new[] { 900 },
            ["middle"] = new[] { 300 },
            ["timeout"] = new[] { 0 },
            ["untested"] = Array.Empty<int>(),
        };
        harness.ViewModel.ApplyBoard(Board(new[] { Group("组", new[] { "untested", "slow", "timeout", "fast", "middle" }) }, delays));

        harness.ViewModel.SortNodesByLatency = true;

        // 源：hideUnavailableProxyNodes 默认 false——排序模式下不可用节点垫尾可见
        ProxyGroupViewModel group = harness.ViewModel.Groups.Single();
        Assert.Equal(new[] { "fast", "middle", "slow", "timeout", "untested" }, group.Nodes.Select(node => node.Name));

        // 开启「隐藏不可用节点」才过滤（源：sortedNodes 的 guard hideUnavailable）
        harness.ViewModel.HideUnavailableProxyNodes = true;
        group = harness.ViewModel.Groups.Single();
        Assert.Equal(new[] { "fast", "middle", "slow" }, group.Nodes.Select(node => node.Name));
    }

    [Fact]
    public void LatencySort_OffKeepsConfigOrder()
    {
        var harness = new Harness();
        var delays = new Dictionary<string, IReadOnlyList<int>>
        {
            ["b"] = new[] { 80 },
            ["a"] = new[] { 500 },
        };
        harness.ViewModel.ApplyBoard(Board(new[] { Group("组", new[] { "a", "b" }) }, delays));

        ProxyGroupViewModel group = harness.ViewModel.Groups.Single();
        Assert.Equal(new[] { "a", "b" }, group.Nodes.Select(node => node.Name));
    }

    [Fact]
    public void SignalBars_HistoryModePadsFiveSlots()
    {
        // 源：LatencySignalBars——5 槽右对齐、不足补空桩（高 2、弱灰）
        var harness = new Harness();
        var delays = new Dictionary<string, IReadOnlyList<int>> { ["a"] = new[] { 100, 250 } };
        harness.ViewModel.ShowLatencyHistory = true;
        harness.ViewModel.ApplyBoard(Board(new[] { Group("组", new[] { "a" }) }, delays));

        ProxyNodeViewModel node = harness.ViewModel.Groups.Single().Nodes.Single();
        Assert.Equal(5, node.SignalBars.Count);
        Assert.Equal(2, node.SignalBars[0].Height);                       // 空桩
        Assert.Equal("#B8BEC6", node.SignalBars[0].ColorHex);
        Assert.Equal(5, node.SignalBars[3].Height);                        // 100ms → excellent
        Assert.Equal("#34A853", node.SignalBars[3].ColorHex);
        Assert.Equal(7.5, node.SignalBars[4].Height);                       // 250ms → good
    }

    [Fact]
    public void SignalBars_SingleModeShowsOnlyLatest()
    {
        var harness = new Harness();
        var delays = new Dictionary<string, IReadOnlyList<int>> { ["a"] = new[] { 100, 1200 } };
        harness.ViewModel.ApplyBoard(Board(new[] { Group("组", new[] { "a" }) }, delays));

        ProxyNodeViewModel node = harness.ViewModel.Groups.Single().Nodes.Single();
        SignalBarViewModel bar = Assert.Single(node.SignalBars);
        Assert.Equal(13, bar.Height);   // 1200ms → poor 满高
        Assert.Equal("#C0392B", bar.ColorHex);
    }

    [Fact]
    public void SignalBars_EmptySamplesHidden()
    {
        var harness = new Harness();
        harness.ViewModel.ShowLatencyHistory = true;
        harness.ViewModel.ApplyBoard(Board(new[] { Group("组", new[] { "a" }) }));

        ProxyNodeViewModel node = harness.ViewModel.Groups.Single().Nodes.Single();
        // 5 根空桩（历史模式始终显示槽位；单根模式无样本隐藏）
        Assert.Equal(5, node.SignalBars.Count);
        harness.ViewModel.ShowLatencyHistory = false;
        Assert.Empty(harness.ViewModel.Groups.Single().Nodes.Single().SignalBars);
        Assert.False(harness.ViewModel.Groups.Single().Nodes.Single().HasSignalBars);
    }

    [Fact]
    public void GroupIcon_CarriedToRow()
    {
        var harness = new Harness();
        harness.ViewModel.ApplyBoard(Board(new[] { Group("组", new[] { "a" }, icon: "https://example.com/icon.png") }));

        Assert.Equal("https://example.com/icon.png", harness.ViewModel.Groups.Single().GroupIcon);
    }

    [Fact]
    public void TestAllGroupsCommand_GuardsAndFires()
    {
        var harness = new Harness();

        harness.ViewModel.TestAllGroupsCommand.Execute(null);
        Assert.Single(harness.TestAllRequests);

        // 进行中不重复触发（源：guard isTestingAllGroupLatencies）
        harness.ViewModel.IsTestingAllGroupLatencies = true;
        harness.ViewModel.TestAllGroupsCommand.Execute(null);
        Assert.Single(harness.TestAllRequests);
    }

    [Fact]
    public void Toggles_ToggleTriggersReapply()
    {
        var harness = new Harness();
        harness.ViewModel.ApplyBoard(Board(new[]
        {
            Group("隐藏组", new[] { "a" }, hidden: true),
            Group("可见组", new[] { "b" }),
        }));

        // hidden 切换即时重排（无需新面板数据）
        harness.ViewModel.ShowHiddenProxyGroups = true;
        Assert.Equal(2, harness.ViewModel.Groups.Count);
        harness.ViewModel.ShowHiddenProxyGroups = false;
        Assert.Single(harness.ViewModel.Groups);
    }
}
