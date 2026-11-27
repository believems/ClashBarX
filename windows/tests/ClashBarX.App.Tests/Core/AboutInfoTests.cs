using ClashBarX.App.Core;
using ClashBarX.Core.L10n;

namespace ClashBarX.App.Tests.Core;

/// <summary>
/// 关于对话框文案装配规格（应用菜单「关于 ClashBarX」的落点；BOSS 拍板 2026-11-27）。
/// 语言显式传入，不读写全局状态（同 CoreL10nTests 的口径），避免跨用例并行串扰。
/// </summary>
public class AboutInfoTests
{
    private const string DataDir = @"C:\Users\test\AppData\Roaming\ClashBarX";

    private static AboutInfo Create(
        string? appVersion = "1.0.1",
        string? kernelVersion = "1.19.34",
        bool kernelRunning = true,
        string? selectedConfig = "ClashBar.yaml",
        CoreLanguage language = CoreLanguage.ChineseSimplified) =>
        AboutInfo.Create(appVersion, kernelVersion, kernelRunning, selectedConfig, DataDir, language);

    [Fact]
    public void 版本号统一带v前缀且数据目录原样透出()
    {
        AboutInfo info = Create();

        Assert.Equal("v1.0.1", info.AppVersionText);
        Assert.Equal("v1.19.34", info.KernelVersionText);
        Assert.Equal("ClashBar.yaml", info.ActiveConfigText);
        Assert.Equal(DataDir, info.DataDirectory);
    }

    [Fact]
    public void 内核停止后不展示残留版本号以免误导()
    {
        // 真机口径：停止后 _lastKnownCoreVersion 仍保留旧版本号（托盘「内核未运行 · v…」同源），
        // 关于对话框只显示「内核未运行」，不能出现「没在跑却有版本」
        AboutInfo chinese = Create(kernelRunning: false, language: CoreLanguage.ChineseSimplified);
        AboutInfo english = Create(kernelRunning: false, language: CoreLanguage.English);

        Assert.Equal("内核未运行", chinese.KernelVersionText);
        Assert.Equal("Core stopped", english.KernelVersionText);
    }

    [Fact]
    public void 内核运行但版本未回报时给中性占位()
    {
        AboutInfo info = Create(kernelVersion: null, kernelRunning: true);

        Assert.Equal("—", info.KernelVersionText);
    }

    [Fact]
    public void 应用版本缺失时给中性占位而不是空串()
    {
        AboutInfo info = Create(appVersion: null);

        Assert.Equal("—", info.AppVersionText);
    }

    [Theory]
    [InlineData(null, CoreLanguage.ChineseSimplified, "未选择")]
    [InlineData("", CoreLanguage.ChineseSimplified, "未选择")]
    [InlineData(null, CoreLanguage.English, "None selected")]
    public void 未选生效配置时按语言给兜底文案(string? selectedConfig, CoreLanguage language, string expected)
    {
        AboutInfo info = Create(selectedConfig: selectedConfig, language: language);

        Assert.Equal(expected, info.ActiveConfigText);
    }

    [Fact]
    public void 装配过程不改全局语言状态()
    {
        CoreLanguage before = CoreL10n.Language;

        Create(language: CoreLanguage.English);

        Assert.Equal(before, CoreL10n.Language);
    }
}
