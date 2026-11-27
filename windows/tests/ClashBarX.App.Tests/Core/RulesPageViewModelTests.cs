using ClashBarX.App.Core;
using ClashBarX.Core.Models;
using ClashBarX.Core.Rules;

namespace ClashBarX.App.Tests.Core;

/// <summary>规则页模型规格测试（源：RulesViewModel 的过滤/搜索/分组语义）。</summary>
public class RulesPageViewModelTests
{
    private static RuleItem Rule(string type, string payload, string proxy) => new(type, payload, proxy);

    private static RulesBoard Board(
        IReadOnlyList<RuleItem> rules,
        Dictionary<string, ProviderDetail>? providers = null,
        int? totalCount = null) => new(
        totalCount ?? rules.Count,
        providers?.Count ?? 0,
        rules,
        providers ?? new Dictionary<string, ProviderDetail>());

    private sealed class Harness
    {
        public int RefreshCalls;
        public bool CanRefresh = true;
        public RulesPageViewModel ViewModel = null!;

        public Harness() => Reset();

        public void Reset()
        {
            ViewModel = new RulesPageViewModel(
                () =>
                {
                    RefreshCalls++;
                    return Task.CompletedTask;
                },
                () => CanRefresh);
        }
    }

    [Fact]
    public void 面板数据喂入生成行与统计()
    {
        var harness = new Harness();
        harness.ViewModel.ApplyBoard(Board(new[]
        {
            Rule("DOMAIN-SUFFIX", "example.com", "DIRECT"),
            Rule("GEOIP", "CN", "Demo Group"),
        }));

        Assert.Equal(2, harness.ViewModel.Rows.Count);
        Assert.Equal("规则 2 · 规则集 0", harness.ViewModel.StatsText);
        RuleRowViewModel first = (RuleRowViewModel)harness.ViewModel.Rows[0];
        Assert.Equal("example.com", first.Target);
        Assert.Equal("DOMAIN-SUFFIX", first.TypeText);
        Assert.Equal("DIRECT", first.Policy);
        Assert.Equal("2/2", harness.ViewModel.CountText);
    }

    [Fact]
    public void 类型过滤五类按子串归类()
    {
        var harness = new Harness();
        harness.ViewModel.ApplyBoard(Board(new[]
        {
            Rule("DOMAIN-SUFFIX", "a.com", "DIRECT"),
            Rule("GEOSITE", "cn", "Demo"),
            Rule("GEOIP", "lan", "DIRECT"),
            Rule("IP-CIDR", "10.0.0.0/8", "DIRECT"),
            Rule("RULE-SET", "rules-a", "Demo"),
            Rule("MATCH", null, "Demo"),
        }));

        harness.ViewModel.TypeFilter = RulesTypeFilter.Domain;
        Assert.Equal(2, harness.ViewModel.Rows.Count);   // DOMAIN-SUFFIX + GEOSITE

        harness.ViewModel.TypeFilter = RulesTypeFilter.Ip;
        Assert.Equal(2, harness.ViewModel.Rows.Count);   // GEOIP + IP-CIDR

        harness.ViewModel.TypeFilter = RulesTypeFilter.RuleSet;
        Assert.Single(harness.ViewModel.Rows);

        harness.ViewModel.TypeFilter = RulesTypeFilter.Other;
        Assert.Single(harness.ViewModel.Rows);   // MATCH

        harness.ViewModel.TypeFilter = RulesTypeFilter.All;
        Assert.Equal(6, harness.ViewModel.Rows.Count);
    }

    [Fact]
    public void 搜索串匹配目标类型与策略()
    {
        var harness = new Harness();
        harness.ViewModel.ApplyBoard(Board(new[]
        {
            Rule("DOMAIN-SUFFIX", "example.com", "DIRECT"),
            Rule("GEOIP", "CN", "Demo Group"),
        }));

        harness.ViewModel.FilterText = "demo";
        Assert.Single(harness.ViewModel.Rows);   // 命中策略列

        harness.ViewModel.FilterText = "GEOIP";
        Assert.Single(harness.ViewModel.Rows);   // 命中类型列

        harness.ViewModel.FilterText = "example";
        RuleRowViewModel row = (RuleRowViewModel)Assert.Single(harness.ViewModel.Rows);
        Assert.Equal("example.com", row.Target);
    }

    [Fact]
    public void 策略过滤与选项回落()
    {
        var harness = new Harness();
        harness.ViewModel.ApplyBoard(Board(new[]
        {
            Rule("MATCH", null, "DIRECT"),
            Rule("GEOIP", "CN", "Demo"),
        }));

        Assert.Equal(3, harness.ViewModel.PolicyOptions.Count);   // 全部 + DIRECT + Demo
        // 经对象级选中项驱动（与 UI 下拉绑定同路径；PolicyFilter 为只读派生态）
        harness.ViewModel.PolicySelection =
            harness.ViewModel.PolicyOptions.First(option => option.Value == "DIRECT");
        Assert.Single(harness.ViewModel.Rows);

        // 选中策略从新数据消失时自动回落「全部」
        harness.ViewModel.ApplyBoard(Board(new[] { Rule("GEOIP", "CN", "Demo") }));
        Assert.Null(harness.ViewModel.PolicyFilter);
        Assert.Single(harness.ViewModel.Rows);
    }

    [Fact]
    public void 按策略分组与组头展开折叠()
    {
        var harness = new Harness();
        harness.ViewModel.ApplyBoard(Board(new[]
        {
            Rule("GEOIP", "CN", "DIRECT"),
            Rule("MATCH", null, "DIRECT"),
            Rule("DOMAIN-SUFFIX", "a.com", "Demo"),
        }));

        harness.ViewModel.GroupByPolicy = true;

        // 组按规则数降序：DIRECT(2) 在前；组头 + 行交替（2 头 + 3 行）
        Assert.Equal(5, harness.ViewModel.Rows.Count);
        RuleGroupHeaderViewModel header = (RuleGroupHeaderViewModel)harness.ViewModel.Rows[0];
        Assert.Equal("DIRECT", header.Policy);
        Assert.Equal(2, header.Count);
        Assert.False(header.IsCollapsed);
        Assert.IsType<RuleRowViewModel>(harness.ViewModel.Rows[1]);

        // 折叠组：只剩组头
        harness.ViewModel.ToggleGroup("DIRECT");
        RuleGroupHeaderViewModel collapsed = (RuleGroupHeaderViewModel)harness.ViewModel.Rows[0];
        Assert.True(collapsed.IsCollapsed);
        Assert.Equal(3, harness.ViewModel.Rows.Count);   // DIRECT 头 + Demo 头 + Demo 行
    }

    [Fact]
    public void 统计列按provider匹配显示规则条数()
    {
        var harness = new Harness();
        harness.ViewModel.ApplyBoard(Board(
            new[] { Rule("RULE-SET", "rules-a", "Demo"), Rule("GEOIP", "CN", "DIRECT") },
            new Dictionary<string, ProviderDetail>
            {
                ["rules-a"] = new("rules-a", "HTTP", null, null, null, 120, null, null),
            }));

        RuleRowViewModel matched = (RuleRowViewModel)harness.ViewModel.Rows[0];
        Assert.Equal("120", matched.StatsText);
        RuleRowViewModel unmatched = (RuleRowViewModel)harness.ViewModel.Rows[1];
        Assert.Equal("0", unmatched.StatsText);
    }

    [Fact]
    public void provider键与名称双写匹配()
    {
        var harness = new Harness();
        harness.ViewModel.ApplyBoard(Board(
            new[] { Rule("RULE-SET", "显示名", "Demo") },
            new Dictionary<string, ProviderDetail>
            {
                ["key-1"] = new("显示名", "HTTP", null, null, null, 42, null, null),
            }));

        RuleRowViewModel row = (RuleRowViewModel)Assert.Single(harness.ViewModel.Rows);
        Assert.Equal("42", row.StatsText);   // 按 provider 的 name 匹配（payload=显示名）
    }

    [Fact]
    public async Task 刷新命令调用注入函数并可禁用()
    {
        var harness = new Harness();
        harness.ViewModel.RefreshCommand.Execute(null);
        Assert.Equal(1, harness.RefreshCalls);

        harness.CanRefresh = false;
        harness.ViewModel.RefreshCommand.Execute(null);
        Assert.Equal(1, harness.RefreshCalls);   // 不可刷新时不调用
        await Task.CompletedTask;
    }
}
