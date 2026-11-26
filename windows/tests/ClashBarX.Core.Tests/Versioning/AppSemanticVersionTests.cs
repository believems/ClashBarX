using ClashBarX.Core.Versioning;

namespace ClashBarX.Core.Tests.Versioning;

/// <summary>语义版本比较规格测试（源：AppRelease.swift 的 AppSemanticVersion）。</summary>
public class AppSemanticVersionTests
{
    [Theory]
    [InlineData("1.19.31", "1.19.31", 0)]
    [InlineData("1.19.31", "1.19.30", 1)]
    [InlineData("1.19.30", "1.19.31", -1)]
    [InlineData("1.20", "1.19.31", 1)]
    [InlineData("v1.19.32", "1.19.31", 1)]          // 前缀字母被首个数字位跳过
    [InlineData("1.19.31-alpha", "1.19.31", 0)]     // 预发布段剥离
    [InlineData("1.19.31+build.7", "1.19.31", 0)]   // build metadata 剥离
    [InlineData("2.0", "1.9.9", 1)]
    [InlineData("1.2", "1.2.0", 0)]                 // 缺失位补 0
    public void Compare_UsesNumericComponents(string left, string right, int expectedSign)
    {
        AppSemanticVersion? leftVersion = AppSemanticVersion.Parse(left);
        AppSemanticVersion? rightVersion = AppSemanticVersion.Parse(right);

        Assert.NotNull(leftVersion);
        Assert.NotNull(rightVersion);
        int actual = leftVersion.Value.CompareTo(rightVersion.Value);
        Assert.Equal(Math.Sign(expectedSign), Math.Sign(actual));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("alpha")]
    [InlineData("meta")]
    public void Parse_UnparseableReturnsNull(string? raw)
    {
        Assert.Null(AppSemanticVersion.Parse(raw));
    }

    [Fact]
    public void DisplayString_JoinsNumericComponents()
    {
        Assert.Equal("1.19.31", AppSemanticVersion.Parse("v1.19.31+meta")!.Value.DisplayString);
    }
}
