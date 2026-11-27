using ClashBarX.Core.Mihomo;

namespace ClashBarX.Core.Tests.Mihomo;

/// <summary>配置顶层 secret 解析规格（本机控制器鉴权；BOSS 生产配置带 secret 的真机问题回归）。</summary>
public class ConfigSecretReaderTests
{
    [Fact]
    public void 顶格无引号secret()
    {
        Assert.Equal("L0vehuan", ConfigSecretReader.TryRead("mixed-port: 8890\nsecret: L0vehuan\nmode: rule\n"));
    }

    [Theory]
    [InlineData("secret: \"a b#c\"\n", "a b#c")]
    [InlineData("secret: 'x y'\n", "x y")]
    public void 引号内空格与井号保留(string content, string expected)
    {
        Assert.Equal(expected, ConfigSecretReader.TryRead(content));
    }

    [Fact]
    public void 无引号行尾注释被剥离()
    {
        Assert.Equal("abc", ConfigSecretReader.TryRead("secret: abc # 注释\n"));
    }

    [Fact]
    public void 缩进或列表或注释里的secret不认()
    {
        string content = "# secret: nope\n  secret: nested\n- secret: list\nsecret: real\n";
        Assert.Equal("real", ConfigSecretReader.TryRead(content));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mode: rule\n")]
    [InlineData("secret:\n")]
    [InlineData("secret: \"\"\n")]
    public void 缺失或空值返回null(string? content)
    {
        Assert.Null(ConfigSecretReader.TryRead(content));
    }
}
