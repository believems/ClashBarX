using ClashBarX.App.Core;

namespace ClashBarX.App.Tests.Core;

/// <summary>TUN 快捷区 VM 规格（源：ProxyTabView.tunStackMenu + AppViewModel 的 tun 状态对）。</summary>
public class TunSectionViewModelTests
{
    private TunSectionViewModel CreateViewModel(Func<bool>? canToggle = null)
    {
        var viewModel = new TunSectionViewModel(canToggle ?? (() => true));
        viewModel.ToggleRequested += _ => { };   // 占位订阅，防未挂接时误判无事件
        return viewModel;
    }

    [Fact]
    public void InitialState_StackOptionsFollowSource()
    {
        var viewModel = CreateViewModel();

        string[] stacks = viewModel.StackOptions.Select(option => option.Stack).ToArray();
        Assert.Equal(new[] { "system", "gvisor", "mixed", "mips" }, stacks);
    }

    [Fact]
    public void SetMipsSupport_DisablesOnlyMips()
    {
        var viewModel = CreateViewModel();

        viewModel.SetMipsSupport(supported: false);
        Assert.True(viewModel.StackOptions.First(option => option.Stack == "system").IsAvailable);
        Assert.False(viewModel.StackOptions.First(option => option.Stack == "mips").IsAvailable);
        Assert.Contains("1.19.31", viewModel.StackOptions.First(option => option.Stack == "mips").DisplayText);

        viewModel.SetMipsSupport(supported: true);
        Assert.True(viewModel.StackOptions.First(option => option.Stack == "mips").IsAvailable);
        Assert.Equal("mips", viewModel.StackOptions.First(option => option.Stack == "mips").DisplayText);
    }

    [Fact]
    public void IsTunEnabled_SetRaisesToggleRequested()
    {
        var viewModel = CreateViewModel();
        bool? requested = null;
        viewModel.ToggleRequested += value => requested = value;

        viewModel.IsTunEnabled = true;

        Assert.True(viewModel.IsTunEnabled);
        Assert.True(requested);
    }

    [Fact]
    public void IsTunEnabled_SameValueDoesNotRefire()
    {
        var viewModel = CreateViewModel();
        int fired = 0;
        viewModel.ToggleRequested += _ => fired++;

        viewModel.IsTunEnabled = true;
        viewModel.IsTunEnabled = true;

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ApplySnapshot_SyncsWithoutFiringEvents()
    {
        var viewModel = CreateViewModel();
        int toggled = 0;
        int stackSelected = 0;
        viewModel.ToggleRequested += _ => toggled++;
        viewModel.StackSelected += _ => stackSelected++;

        viewModel.ApplySnapshot(enabled: true, stack: "gvisor");

        Assert.True(viewModel.IsTunEnabled);
        Assert.Equal("gvisor", viewModel.SelectedStack);
        Assert.Equal(0, toggled);
        Assert.Equal(0, stackSelected);
    }

    [Fact]
    public void SelectedStack_SetRaisesStackSelected()
    {
        var viewModel = CreateViewModel();
        string? selected = null;
        viewModel.StackSelected += value => selected = value;

        viewModel.SelectedStack = "system";

        Assert.Equal("system", viewModel.SelectedStack);
        Assert.Equal("system", selected);
    }

    [Fact]
    public void SelectedStack_EmptyAndSameIgnored()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedStack = "mixed";
        int fired = 0;
        viewModel.StackSelected += _ => fired++;

        viewModel.SelectedStack = "";
        viewModel.SelectedStack = null;
        viewModel.SelectedStack = "mixed";   // 同值不触发（VM 防抖）

        Assert.Equal(0, fired);
    }

    [Fact]
    public void SetSyncing_DisablesToggle()
    {
        bool coreRunning = true;
        var viewModel = new TunSectionViewModel(() => coreRunning);

        Assert.True(viewModel.IsTunToggleEnabled);

        viewModel.SetSyncing(true);
        Assert.False(viewModel.IsTunToggleEnabled);

        viewModel.SetSyncing(false);
        coreRunning = false;
        Assert.False(viewModel.IsTunToggleEnabled);
    }

    [Fact]
    public void IsTunToggleEnabled_FollowsCoreRunning()
    {
        bool coreRunning = false;
        var viewModel = new TunSectionViewModel(() => coreRunning);

        Assert.False(viewModel.IsTunToggleEnabled);
        coreRunning = true;
        Assert.True(viewModel.IsTunToggleEnabled);
    }
}
