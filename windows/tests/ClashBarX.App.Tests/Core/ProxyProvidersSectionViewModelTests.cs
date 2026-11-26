using ClashBarX.App.Core;
using ClashBarX.Core.Models;

namespace ClashBarX.App.Tests.Core;

/// <summary>Provider 区 VM 规格（源：proxyProvidersSection/proxyProviderRow 状态语义）。</summary>
public class ProxyProvidersSectionViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static ProviderDetail Detail(
        int nodeCount,
        string? updatedAt = null,
        long? expire = null,
        long? upload = null,
        long? download = null,
        long? total = null) => new(
        "demo", "HTTP", null, null, updatedAt, null,
        upload is null || download is null || total is null
            ? null
            : new ProviderSubscriptionInfo(upload, download, total, expire),
        Enumerable.Range(0, nodeCount).Select(index => new ProviderProxyNode($"n{index}", "SS", Array.Empty<int>())).ToArray());

    [Fact]
    public void ApplyProviders_SortsByNameAndFillsRows()
    {
        var viewModel = new ProxyProvidersSectionViewModel();
        var providers = new Dictionary<string, ProviderDetail>
        {
            ["zeta"] = Detail(2, Now.AddHours(-3).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")),
            ["alpha"] = Detail(5, Now.AddMinutes(-10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")),
        };

        viewModel.ApplyProviders(providers, now: Now);

        Assert.Equal(new[] { "alpha", "zeta" }, viewModel.Rows.Select(row => row.Name));
        Assert.Equal(5, viewModel.Rows[0].NodeCount);
        Assert.Equal("10 分钟前", viewModel.Rows[0].UpdatedText);
        Assert.True(viewModel.HasProviders);
    }

    [Fact]
    public void ApplyProviders_EmptyDictionaryHidesSection()
    {
        var viewModel = new ProxyProvidersSectionViewModel();

        viewModel.ApplyProviders(new Dictionary<string, ProviderDetail>());

        Assert.Empty(viewModel.Rows);
        Assert.False(viewModel.HasProviders);
    }

    [Fact]
    public void ApplyDetail_ExpiryWarningAndQuota()
    {
        var viewModel = new ProxyProvidersSectionViewModel();
        long nowSeconds = Now.ToUnixTimeSeconds();
        var providers = new Dictionary<string, ProviderDetail>
        {
            ["soon"] = Detail(1, expire: nowSeconds + 10 * 86400, upload: 80L * 1024 * 1024 * 1024, download: 0, total: 100L * 1024 * 1024 * 1024),
            ["safe"] = Detail(1, expire: nowSeconds + 90 * 86400, upload: 1L * 1024 * 1024 * 1024, download: 0, total: 100L * 1024 * 1024 * 1024),
        };

        viewModel.ApplyProviders(providers, now: Now);

        ProviderRowViewModel soon = viewModel.Rows.First(row => row.Name == "soon");
        Assert.True(soon.ExpiresSoon);
        Assert.Equal("10 天后到期", soon.ExpireText);
        Assert.Equal("80GB / 100GB", soon.QuotaText);
        Assert.Equal("warn", soon.QuotaLevel);   // 0.8 ≥ 0.75

        ProviderRowViewModel safe = viewModel.Rows.First(row => row.Name == "safe");
        Assert.False(safe.ExpiresSoon);
        Assert.Equal("normal", safe.QuotaLevel);
    }

    [Fact]
    public void ApplyDetail_NoSubscriptionHidesQuota()
    {
        var viewModel = new ProxyProvidersSectionViewModel();

        viewModel.ApplyProviders(new Dictionary<string, ProviderDetail> { ["a"] = Detail(3) });

        ProviderRowViewModel row = viewModel.Rows.Single();
        Assert.Null(row.QuotaText);
        Assert.Null(row.UsedRatio);
        Assert.Equal("未知", row.ExpireText);
        Assert.Equal("normal", row.QuotaLevel);
    }

    [Fact]
    public void QuotaLevel_CriticalAt90Percent()
    {
        var viewModel = new ProxyProvidersSectionViewModel();
        long used = 95L * 1024 * 1024 * 1024;
        var providers = new Dictionary<string, ProviderDetail>
        {
            ["hot"] = Detail(1, upload: used, download: 0, total: 100L * 1024 * 1024 * 1024),
        };

        viewModel.ApplyProviders(providers, now: Now);

        Assert.Equal("critical", viewModel.Rows.Single().QuotaLevel);
    }

    [Fact]
    public void Commands_ForwardRequestsToEvents()
    {
        var viewModel = new ProxyProvidersSectionViewModel();
        int updateAll = 0;
        string? updatedProvider = null;
        string? healthchecked = null;
        viewModel.UpdateAllRequested += () => updateAll++;
        viewModel.UpdateProviderRequested += name => updatedProvider = name;
        viewModel.HealthcheckProviderRequested += name => healthchecked = name;

        viewModel.UpdateAllCommand.Execute(null);
        viewModel.UpdateProviderCommand.Execute("alpha");
        viewModel.HealthcheckProviderCommand.Execute("beta");

        Assert.Equal(1, updateAll);
        Assert.Equal("alpha", updatedProvider);
        Assert.Equal("beta", healthchecked);
    }

    [Fact]
    public void SetProviderUpdating_TogglesRowState()
    {
        var viewModel = new ProxyProvidersSectionViewModel();
        viewModel.ApplyProviders(new Dictionary<string, ProviderDetail> { ["alpha"] = Detail(1) });

        viewModel.SetProviderUpdating("alpha", true);
        Assert.True(viewModel.Rows.Single().IsUpdating);

        viewModel.SetProviderUpdating("alpha", false);
        Assert.False(viewModel.Rows.Single().IsUpdating);
    }
}
