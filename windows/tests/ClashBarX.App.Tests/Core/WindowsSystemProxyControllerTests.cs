using ClashBarX.App.Core;
using ClashBarX.Core.SystemProxy;
using Microsoft.Win32;

namespace ClashBarX.App.Tests.Core;

/// <summary>
/// 注册表实现的真注册表测试：用 HKCU 下的随机隔离子键，broadcast 关闭以不打扰系统。
/// </summary>
public class WindowsSystemProxyControllerTests : IDisposable
{
    private readonly string _testKeyPath;

    public WindowsSystemProxyControllerTests()
    {
        _testKeyPath = $@"Software\ClashBarX-Tests\{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_testKeyPath, throwOnMissingSubKey: false);
    }

    private WindowsSystemProxyController CreateController() =>
        new(registryPath: _testKeyPath, broadcast: false);

    [Fact]
    public void 无值时读取为禁用状态()
    {
        WindowsSystemProxyController controller = CreateController();

        SystemProxySnapshot snapshot = controller.Read();

        Assert.False(snapshot.Enabled);
        Assert.Null(snapshot.Server);
        Assert.Empty(snapshot.BypassList);
    }

    [Fact]
    public void 启用后读回目标与绕过列表()
    {
        WindowsSystemProxyController controller = CreateController();
        var target = new SystemProxyTarget("127.0.0.1", 7890, SystemProxyDefaults.DefaultBypassList);

        controller.Enable(target);
        SystemProxySnapshot snapshot = controller.Read();

        Assert.True(snapshot.Enabled);
        Assert.Equal("127.0.0.1:7890", snapshot.Server);
        Assert.Equal(
            SystemProxyBypass.Normalize(SystemProxyDefaults.DefaultBypassList),
            snapshot.BypassList);
    }

    [Fact]
    public void 禁用只翻开关并保留服务器值()
    {
        WindowsSystemProxyController controller = CreateController();
        controller.Enable(new SystemProxyTarget("127.0.0.1", 7890, SystemProxyDefaults.DefaultBypassList));

        controller.Disable();
        SystemProxySnapshot snapshot = controller.Read();

        Assert.False(snapshot.Enabled);
        Assert.Equal("127.0.0.1:7890", snapshot.Server);   // 保留，便于再次启用
        Assert.Equal(
            SystemProxyBypass.Normalize(SystemProxyDefaults.DefaultBypassList),
            snapshot.BypassList);
    }

    [Fact]
    public void 写入值类型与键位符合InternetSettings约定()
    {
        WindowsSystemProxyController controller = CreateController();
        controller.Enable(new SystemProxyTarget("127.0.0.1", 7890, new[] { "localhost", "10.*" }));

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_testKeyPath);
        Assert.NotNull(key);
        Assert.Equal(1, key.GetValue("ProxyEnable"));
        Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("ProxyEnable"));
        Assert.Equal("127.0.0.1:7890", key.GetValue("ProxyServer"));
        Assert.Equal(RegistryValueKind.String, key.GetValueKind("ProxyServer"));
        Assert.Equal("localhost;10.*", key.GetValue("ProxyOverride"));
    }
}
