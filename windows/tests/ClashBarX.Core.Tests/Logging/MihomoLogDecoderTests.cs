using System.Text;
using ClashBarX.Core.Logging;

namespace ClashBarX.Core.Tests.Logging;

/// <summary>mihomo 日志帧解码规格测试（源：decodeLogLinePayload + normalizeLogLevel）。</summary>
public class MihomoLogDecoderTests
{
    private static ReadOnlySpan<byte> Frame(string json) => Encoding.UTF8.GetBytes(json).AsSpan();

    [Fact]
    public void 标准形态_type作为等级_payload作为消息()
    {
        Assert.True(MihomoLogDecoder.TryDecode(
            Frame("{\"type\":\"warning\",\"payload\":\"start initial compatible provider\"}"),
            out string level, out string message));
        Assert.Equal("warning", level);
        Assert.Equal("start initial compatible provider", message);
    }

    [Theory]
    [InlineData("error", "error")]
    [InlineData("ERROR", "error")]
    [InlineData("err", "error")]
    [InlineData("warning", "warning")]
    [InlineData("WARN", "warning")]
    [InlineData("info", "info")]
    [InlineData("debug", "info")]
    [InlineData("", "info")]
    public void 等级归一化(string raw, string expected)
    {
        Assert.Equal(expected, MihomoLogDecoder.NormalizeLevel(raw));
    }

    [Fact]
    public void 空payload回退到裸文本()
    {
        // LogLine 解析成功但 payload 空 → 裸文本路径
        Assert.True(MihomoLogDecoder.TryDecode(Frame("plain text line"), out _, out string message));
        Assert.Equal("plain text line", message);
    }

    [Fact]
    public void 数组形态取首条()
    {
        Assert.True(MihomoLogDecoder.TryDecode(
            Frame("{\"logs\":[{\"type\":\"error\",\"payload\":\"first failure\"},{\"type\":\"info\",\"payload\":\"second\"}]}"),
            out string level, out string message));
        Assert.Equal("error", level);
        Assert.Equal("first failure", message);
    }

    [Fact]
    public void 空白帧返回失败()
    {
        Assert.False(MihomoLogDecoder.TryDecode(Frame("   "), out _, out _));
    }

    [Fact]
    public void 空结构按裸文本处理()
    {
        // LogLine/数组形态都取不到 payload → 裸文本 "{}" 非空即按 info 入列
        //（源：decodeLogLinePayload 的第三回退；"{}"/"null" 噪声由 StreamPayload.Normalize 前置过滤）
        Assert.True(MihomoLogDecoder.TryDecode(Frame("{}"), out string level, out string message));
        Assert.Equal("info", level);
        Assert.Equal("{}", message);
    }

    [Fact]
    public void 半_json_文本按裸文本处理()
    {
        Assert.True(MihomoLogDecoder.TryDecode(Frame("{\"broken json"), out string level, out string message));
        Assert.Equal("info", level);
        Assert.Equal("{\"broken json", message);
    }
}
