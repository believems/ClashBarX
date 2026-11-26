namespace ClashBarX.Core.Mihomo;

/// <summary>WebSocket 帧消息：文本帧携带 Text，二进制帧携带 Binary（其一必非空）。</summary>
public readonly struct WebSocketStreamMessage
{
    public string? Text { get; }
    public ReadOnlyMemory<byte> Binary { get; }

    private WebSocketStreamMessage(string? text, ReadOnlyMemory<byte> binary)
    {
        Text = text;
        Binary = binary;
    }

    public static WebSocketStreamMessage FromText(string text) => new(text, default);

    public static WebSocketStreamMessage FromBinary(ReadOnlyMemory<byte> data) => new(null, data);

    public bool IsText => Text != null;
}

/// <summary>尚未连接的 WebSocket 连接抽象：连接失败与断线都由 ReceiveAsync 阶段的异常表达。</summary>
public interface IWebSocketConnection : IAsyncDisposable
{
    /// <summary>建立连接。失败抛异常，由流协调器按断线重连处理。</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>接收一条完整消息（含分片重组与 4MB 上限）。断线或超限抛异常。</summary>
    Task<WebSocketStreamMessage> ReceiveAsync(CancellationToken cancellationToken);
}
