using ClashBarX.App.Core;
using ClashBarX.Core.Ssid;

namespace ClashBarX.App.Tests.Core;

/// <summary>SSID 策略区 VM 规格（源：ProxyTabView 的 ssidStrategy 快捷项状态语义）。</summary>
public class SsidStrategySectionViewModelTests
{
    [Fact]
    public void IsEnabled_SetRaisesToggleEvent()
    {
        var viewModel = new SsidStrategySectionViewModel();
        bool? toggledTo = null;
        viewModel.EnabledToggled += value => toggledTo = value;

        viewModel.IsEnabled = true;

        Assert.True(viewModel.IsEnabled);
        Assert.True(toggledTo);
    }

    [Fact]
    public void IsEnabled_SameValueDoesNotRefire()
    {
        var viewModel = new SsidStrategySectionViewModel();
        int fired = 0;
        viewModel.EnabledToggled += _ => fired++;

        viewModel.IsEnabled = true;
        viewModel.IsEnabled = true;

        Assert.Equal(1, fired);
    }

    [Fact]
    public void RestoreEnabled_SyncsWithoutFiring()
    {
        var viewModel = new SsidStrategySectionViewModel();
        int fired = 0;
        viewModel.EnabledToggled += _ => fired++;

        viewModel.RestoreEnabled(true);

        Assert.True(viewModel.IsEnabled);
        Assert.Equal(0, fired);
    }

    [Theory]
    [InlineData(null, "未连接 Wi-Fi")]
    [InlineData("", "未连接 Wi-Fi")]
    [InlineData("  ", "未连接 Wi-Fi")]
    [InlineData("Home-5G", "Home-5G")]
    public void CurrentSsidText_FallsBackWhenUnconnected(string? ssid, string expected)
    {
        var viewModel = new SsidStrategySectionViewModel();

        viewModel.ApplyCurrentSsid(ssid);

        Assert.Equal(expected, viewModel.CurrentSsidText);
    }

    [Fact]
    public void ApplyCurrentSsid_SameValueDoesNotRefire()
    {
        var viewModel = new SsidStrategySectionViewModel();
        viewModel.ApplyCurrentSsid("cafe");
        int changed = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SsidStrategySectionViewModel.CurrentSsid))
            {
                changed++;
            }
        };

        viewModel.ApplyCurrentSsid("cafe");

        Assert.Equal(0, changed);
    }

    [Fact]
    public void ApplyBindings_RebuildsNormalizedRows()
    {
        var viewModel = new SsidStrategySectionViewModel();

        viewModel.ApplyBindings(new[]
        {
            new SsidStrategyRule(" home ", "a.yaml"),
            new SsidStrategyRule("home", "b.yaml"),   // 同 SSID 覆盖
            new SsidStrategyRule("", "drop.yaml"),     // 空丢弃
        });

        var row = Assert.Single(viewModel.Bindings);
        Assert.Equal("home", row.Ssid);
        Assert.Equal("b.yaml", row.ConfigFileName);
    }

    [Fact]
    public void Requests_ForwardToEvents()
    {
        var viewModel = new SsidStrategySectionViewModel();
        int bind = 0;
        string? removed = null;
        viewModel.BindCurrentRequested += () => bind++;
        viewModel.RemoveBindingRequested += ssid => removed = ssid;

        viewModel.RequestBindCurrent();
        viewModel.RequestRemove("cafe");

        Assert.Equal(1, bind);
        Assert.Equal("cafe", removed);
    }
}
