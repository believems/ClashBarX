using System.IO;
using ClashBarX.App.Core;
using ClashBarX.App.Tests.TestInfrastructure;

namespace ClashBarX.App.Tests.Core;

/// <summary>首次使用引导链（装内核 → 导订阅 → 启动 → 内容出现）判定规格测试。</summary>
public class FirstRunGuidePolicyTests
{
    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "clashbarx-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void 选中配置文件存在即可启动()
    {
        string directory = CreateTempDirectory();
        try
        {
            string selected = Path.Combine(directory, "sub.yaml");
            File.WriteAllText(selected, "proxies: []");

            Assert.True(FirstRunGuidePolicy.HasRunnableConfig(selected, Path.Combine(directory, "config.yaml")));
        }
        finally
        {
            AppTestHelper.CleanupDirectory(directory);
        }
    }

    [Fact]
    public void 未选中配置但根目录手工配置存在也可启动()
    {
        string directory = CreateTempDirectory();
        try
        {
            string defaultConfig = Path.Combine(directory, "config.yaml");
            File.WriteAllText(defaultConfig, "proxies: []");

            Assert.True(FirstRunGuidePolicy.HasRunnableConfig(null, defaultConfig));
            Assert.True(FirstRunGuidePolicy.HasRunnableConfig(Path.Combine(directory, "missing.yaml"), defaultConfig));
        }
        finally
        {
            AppTestHelper.CleanupDirectory(directory);
        }
    }

    [Fact]
    public void 既无选中配置也无手工配置则不可启动()
    {
        string directory = CreateTempDirectory();
        try
        {
            string defaultConfig = Path.Combine(directory, "config.yaml");

            Assert.False(FirstRunGuidePolicy.HasRunnableConfig(null, defaultConfig));   // 新用户：还没导入订阅
            Assert.False(FirstRunGuidePolicy.HasRunnableConfig(Path.Combine(directory, "missing.yaml"), defaultConfig));
        }
        finally
        {
            AppTestHelper.CleanupDirectory(directory);
        }
    }

    [Theory]
    [InlineData(CoreSetupGuideTrigger.Manual, false, true)]
    [InlineData(CoreSetupGuideTrigger.Manual, true, true)]      // 手动触发：弹过也再弹（用户正要启动内核）
    [InlineData(CoreSetupGuideTrigger.Startup, false, true)]    // 首次启动：弹
    [InlineData(CoreSetupGuideTrigger.Startup, true, false)]    // 其后启动：不再自动弹
    public void 无内核引导_手动触发恒弹_启动触发仅首次(CoreSetupGuideTrigger trigger, bool shownBefore, bool expected)
    {
        Assert.Equal(expected, FirstRunGuidePolicy.ShouldShowCoreSetupGuide(trigger, shownBefore));
    }

    [Theory]
    [InlineData(0, true, true)]      // 首次导入且内核就位 → 引导性成功提示
    [InlineData(0, false, false)]    // 首次导入但内核缺失 → 由无内核安装引导接管，不重复弹
    [InlineData(1, true, false)]     // 非首次导入 → 只记日志，不打断
    [InlineData(3, false, false)]
    public void 导入成功反馈只在首次导入且内核就位时弹(int configCountBeforeImport, bool coreBinaryPresent, bool expected)
    {
        Assert.Equal(
            expected,
            FirstRunGuidePolicy.ShouldAnnounceImportSuccess(configCountBeforeImport, coreBinaryPresent));
    }
}
