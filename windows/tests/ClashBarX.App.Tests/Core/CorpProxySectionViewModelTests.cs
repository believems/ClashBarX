using ClashBarX.App.Core;

namespace ClashBarX.App.Tests.Core;

/// <summary>内网前置代理区块 VM 规格（Issue #2）：Changed=持久化、Committed=热重载的双事件语义与端口校验。</summary>
public class CorpProxySectionViewModelTests
{
    private static (CorpProxySectionViewModel vm, int changed, int committed) Create()
    {
        var vm = new CorpProxySectionViewModel();
        int changed = 0, committed = 0;
        vm.Changed += () => changed++;
        vm.Committed += () => committed++;
        return (vm, changed, committed);
    }

    [Fact]
    public void 字段变化只发Changed不发Committed()
    {
        var vm = new CorpProxySectionViewModel();
        int changed = 0, committed = 0;
        vm.Changed += () => changed++;
        vm.Committed += () => committed++;

        vm.Host = "proxy.corp.local";

        Assert.Equal(1, changed);
        Assert.Equal(0, committed);
    }

    [Fact]
    public void 开关切换同时发Changed与Committed()
    {
        var vm = new CorpProxySectionViewModel();
        int changed = 0, committed = 0;
        vm.Changed += () => changed++;
        vm.Committed += () => committed++;

        vm.IsEnabled = true;

        Assert.Equal(1, changed);
        Assert.Equal(1, committed);   // 源：onChange(of: enabled) → reload
    }

    [Fact]
    public void Commit显式提交发Committed()
    {
        var vm = new CorpProxySectionViewModel();
        int committed = 0;
        vm.Committed += () => committed++;

        vm.Commit();

        Assert.Equal(1, committed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    [InlineData("")]
    public void 端口非法输入不回写(string text)
    {
        var vm = new CorpProxySectionViewModel();
        int changed = 0;
        vm.Changed += () => changed++;

        vm.PortText = text;

        Assert.Equal(8080, vm.Port);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void 端口合法输入回写并通知()
    {
        var vm = new CorpProxySectionViewModel();
        int changed = 0;
        vm.Changed += () => changed++;

        vm.PortText = " 3128 ";

        Assert.Equal(3128, vm.Port);
        Assert.Equal(1, changed);
        Assert.Equal("3128", vm.PortText);
    }

    [Fact]
    public void Restore不触发任何事件且填充字段()
    {
        var vm = new CorpProxySectionViewModel();
        int changed = 0, committed = 0;
        vm.Changed += () => changed++;
        vm.Committed += () => committed++;

        vm.Restore(new ClashBarX.Core.CorpProxy.CorpProxySettings(true, "h", 3128, "u", "p"));

        Assert.Equal(0, changed);
        Assert.Equal(0, committed);
        Assert.True(vm.ToSettings().IsReady);
        Assert.Equal("p", vm.Password);
    }

    [Fact]
    public void 相同值不重复通知()
    {
        var vm = new CorpProxySectionViewModel();
        vm.Host = "h";
        int changed = 0;
        vm.Changed += () => changed++;

        vm.Host = "h";

        Assert.Equal(0, changed);
    }
}
