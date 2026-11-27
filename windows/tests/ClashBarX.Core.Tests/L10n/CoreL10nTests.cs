using ClashBarX.Core.L10n;

namespace ClashBarX.Core.Tests.L10n;

/// <summary>代码侧文案字典规格测试（中英文键集合一致；按语言查询不改全局状态）。</summary>
public class CoreL10nTests
{
    [Fact]
    public void 中英文字典键集合一致()
    {
        IReadOnlyCollection<string> chinese = CoreL10n.Keys(CoreLanguage.ChineseSimplified);
        IReadOnlyCollection<string> english = CoreL10n.Keys(CoreLanguage.English);

        Assert.Equal(chinese.Order(StringComparer.Ordinal), english.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(CoreLanguage.ChineseSimplified, "settings.appearance.system", "跟随系统")]
    [InlineData(CoreLanguage.English, "settings.appearance.system", "Follow system")]
    [InlineData(CoreLanguage.ChineseSimplified, "proxy.empty.core_not_running", "启动内核后此处显示代理分组与订阅源")]
    [InlineData(CoreLanguage.English, "proxy.empty.core_not_running", "Start the core to show proxy groups and providers here")]
    public void 按指定语言查文案且不改全局语言(CoreLanguage language, string key, string expected)
    {
        CoreLanguage before = CoreL10n.Language;

        Assert.Equal(expected, CoreL10n.T(language, key));
        Assert.Equal(before, CoreL10n.Language);
    }

    [Fact]
    public void 缺失键按两种语言都原样返回()
    {
        Assert.Equal("no.such.key", CoreL10n.T(CoreLanguage.English, "no.such.key"));
        Assert.Equal("no.such.key", CoreL10n.T(CoreLanguage.ChineseSimplified, "no.such.key"));
    }
}
