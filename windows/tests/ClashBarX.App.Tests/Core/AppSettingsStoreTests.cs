using System.IO;
using System.Text.Json;
using ClashBarX.App.Core;

namespace ClashBarX.App.Tests.Core;

/// <summary>设置持久化规格测试（含旧 JSON 兼容——新字段带默认值）。</summary>
public class AppSettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"clashbarx-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void 文件不存在返回默认值()
    {
        var store = new AppSettingsStore(_path);

        AppSettings settings = store.Load();

        Assert.False(settings.SystemProxyEnabled);
        Assert.False(settings.ProxyGuardEnabled);
        Assert.Equal(300, settings.ProxyGuardIntervalSeconds);
        Assert.False(settings.CoreSetupGuideShown);
    }

    [Fact]
    public void 旧版JSON只有系统代理字段时守护字段取默认值()
    {
        File.WriteAllText(_path, "{\"SystemProxyEnabled\":true}");

        AppSettings settings = new AppSettingsStore(_path).Load();

        Assert.True(settings.SystemProxyEnabled);   // 旧字段保留
        Assert.False(settings.ProxyGuardEnabled);   // 新字段默认
        Assert.Equal(300, settings.ProxyGuardIntervalSeconds);
        Assert.False(settings.CoreSetupGuideShown);   // 新字段默认：升级上来的用户首次启动仍会看到无内核引导
    }

    [Fact]
    public void 无内核引导已展示标记可往返()
    {
        var store = new AppSettingsStore(_path);
        store.Save(new AppSettings(SystemProxyEnabled: false, CoreSetupGuideShown: true));

        Assert.True(store.Load().CoreSetupGuideShown);
    }

    [Fact]
    public void 新版字段完整往返()
    {
        var store = new AppSettingsStore(_path);
        store.Save(new AppSettings(
            SystemProxyEnabled: true,
            ProxyGuardEnabled: true,
            ProxyGuardIntervalSeconds: 45));

        AppSettings loaded = store.Load();

        Assert.True(loaded.SystemProxyEnabled);
        Assert.True(loaded.ProxyGuardEnabled);
        Assert.Equal(45, loaded.ProxyGuardIntervalSeconds);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"SystemProxyEnabled\":")]
    public void 损坏JSON回退默认值(string content)
    {
        File.WriteAllText(_path, content);

        AppSettings settings = new AppSettingsStore(_path).Load();

        Assert.Equal(AppSettings.Default, settings);
    }

    [Fact]
    public void Save写入缩进JSON且字段完整()
    {
        var store = new AppSettingsStore(_path);
        store.Save(new AppSettings(false, true, 60));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.True(document.RootElement.GetProperty("ProxyGuardEnabled").GetBoolean());
        Assert.Equal(60, document.RootElement.GetProperty("ProxyGuardIntervalSeconds").GetInt32());
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
