using ClashBarX.Core.Ssid;

namespace ClashBarX.Core.Tests.Ssid;

/// <summary>SSID 策略规则与解析规格测试（源：SSIDStrategyRule.swift + resolveSSIDStrategyConfig）。</summary>
public class SsidStrategyTests
{
    private static SsidStrategyRule Rule(string ssid, string config) => new(ssid, config);

    [Fact]
    public void Normalize_TrimsAndDropsEmptyFields()
    {
        var rules = new[]
        {
            Rule("  home  ", " a.yaml "),
            Rule("", "b.yaml"),        // SSID 空丢弃
            Rule("office", ""),        // 配置空丢弃
        };

        IReadOnlyList<SsidStrategyRule> normalized = SsidStrategyRule.Normalize(rules);

        var rule = Assert.Single(normalized);
        Assert.Equal("home", rule.Ssid);
        Assert.Equal("a.yaml", rule.ConfigFileName);
    }

    [Fact]
    public void Normalize_DuplicateSsidLastWins()
    {
        // 源：同 SSID 后写覆盖前写
        var rules = new[]
        {
            Rule("home", "a.yaml"),
            Rule("home", "b.yaml"),
        };

        IReadOnlyList<SsidStrategyRule> normalized = SsidStrategyRule.Normalize(rules);

        var rule = Assert.Single(normalized);
        Assert.Equal("b.yaml", rule.ConfigFileName);
    }

    [Fact]
    public void Upsert_ReplacesExistingSsidBinding()
    {
        var rules = new[] { Rule("home", "a.yaml"), Rule("office", "b.yaml") };

        IReadOnlyList<SsidStrategyRule> upserted = SsidStrategyRule.Upsert("home", "c.yaml", rules);

        Assert.Equal(2, upserted.Count);
        Assert.Equal("c.yaml", upserted.Single(rule => rule.Ssid == "home").ConfigFileName);
    }

    [Fact]
    public void Upsert_EmptyInputsReturnNormalized()
    {
        var rules = new[] { Rule("home", "a.yaml") };

        Assert.Single(SsidStrategyRule.Upsert("", "x.yaml", rules));
        Assert.Single(SsidStrategyRule.Upsert("home", " ", rules));
    }

    [Fact]
    public void Remove_DropsMatchingSsid()
    {
        var rules = new[] { Rule("home", "a.yaml"), Rule("office", "b.yaml") };

        IReadOnlyList<SsidStrategyRule> removed = SsidStrategyRule.Remove("home", rules);

        var rule = Assert.Single(removed);
        Assert.Equal("office", rule.Ssid);
    }

    [Fact]
    public void Resolve_NoSsidOrNoRuleIsNoAction()
    {
        Assert.Equal(
            SsidStrategyResolutionKind.NoAction,
            SsidStrategyResolver.Resolve(null, "a.yaml", new[] { Rule("home", "a.yaml") }, new[] { "a.yaml" }).Kind);
        Assert.Equal(
            SsidStrategyResolutionKind.NoAction,
            SsidStrategyResolver.Resolve("cafe", "a.yaml", new[] { Rule("home", "b.yaml") }, new[] { "a.yaml", "b.yaml" }).Kind);
    }

    [Fact]
    public void Resolve_MissingConfigReportsTarget()
    {
        SsidStrategyResolution resolution = SsidStrategyResolver.Resolve(
            "cafe", "a.yaml", new[] { Rule("cafe", "gone.yaml") }, new[] { "a.yaml" });

        Assert.Equal(SsidStrategyResolutionKind.MissingConfig, resolution.Kind);
        Assert.Equal("gone.yaml", resolution.ConfigFileName);
    }

    [Fact]
    public void Resolve_SameConfigIsNoAction()
    {
        SsidStrategyResolution resolution = SsidStrategyResolver.Resolve(
            "cafe", " b.yaml ", new[] { Rule("cafe", "b.yaml") }, new[] { "b.yaml" });

        Assert.Equal(SsidStrategyResolutionKind.NoAction, resolution.Kind);
    }

    [Fact]
    public void Resolve_MatchSwitchesConfig()
    {
        SsidStrategyResolution resolution = SsidStrategyResolver.Resolve(
            "  cafe  ", "a.yaml", new[] { Rule("cafe", "b.yaml") }, new[] { "a.yaml", " b.yaml " });

        Assert.Equal(SsidStrategyResolutionKind.SwitchToConfig, resolution.Kind);
        Assert.Equal("b.yaml", resolution.ConfigFileName);
    }
}
