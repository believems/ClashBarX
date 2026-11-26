using System.Text;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Streams;

namespace ClashBarX.Core.Tests.Streams;

public class StreamPayloadTests
{
    [Fact]
    public void 有效文本以UTF8字节广播()
    {
        var message = WebSocketStreamMessage.FromText("{\"up\":1,\"down\":2}");
        var payload = StreamPayload.Normalize(message);

        Assert.NotNull(payload);
        Assert.Equal("{\"up\":1,\"down\":2}", Encoding.UTF8.GetString(payload!.Value.Span));
    }

    [Fact]
    public void 文本首尾空白被裁剪后广播()
    {
        var message = WebSocketStreamMessage.FromText("  \n {\"up\":1} \r\n ");
        var payload = StreamPayload.Normalize(message);

        Assert.NotNull(payload);
        Assert.Equal("{\"up\":1}", Encoding.UTF8.GetString(payload!.Value.Span));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    [InlineData("null")]
    [InlineData("{}")]
    public void 噪声文本被丢弃(string text)
    {
        Assert.Null(StreamPayload.Normalize(WebSocketStreamMessage.FromText(text)));
    }

    [Fact]
    public void 噪声判定大小写敏感()
    {
        // "NULL" 不是 "null"，不视为噪声
        var payload = StreamPayload.Normalize(WebSocketStreamMessage.FromText("NULL"));
        Assert.NotNull(payload);
        Assert.Equal("NULL", Encoding.UTF8.GetString(payload!.Value.Span));
    }

    [Fact]
    public void 含空白的对象字面量不属于噪声()
    {
        var payload = StreamPayload.Normalize(WebSocketStreamMessage.FromText("{ }"));
        Assert.NotNull(payload);
        Assert.Equal("{ }", Encoding.UTF8.GetString(payload!.Value.Span));
    }

    [Fact]
    public void 空二进制被丢弃()
    {
        var message = WebSocketStreamMessage.FromBinary(Array.Empty<byte>());
        Assert.Null(message.Text);
        Assert.Equal(0, message.Binary.Length);
        Assert.True(message.Binary.IsEmpty);
        Assert.Null(StreamPayload.Normalize(message));
    }

    [Fact]
    public void 二进制载荷原样广播不做null检查()
    {
        // 二进制不做 "null"/"{}" 噪声判定（源：normalizeWebSocketPayload 的 data 分支）
        var payload = StreamPayload.Normalize(WebSocketStreamMessage.FromBinary("null"u8.ToArray()));
        Assert.NotNull(payload);
        Assert.Equal("null"u8.ToArray(), payload!.Value.ToArray());
    }
}
