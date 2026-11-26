using System.Net.WebSockets;
using System.Text;

namespace ClashBarX.Core.Mihomo;

/// <summary>
/// WebSocket 整帧读取（源：URLSessionWebSocketTask.receive 的单条消息语义 + maximumMessageSize）。
/// 分片重组由调用方缓冲区完成；累计长度超过 4 MB 时立即失败。
/// </summary>
public static class WebSocketMessageReader
{
    /// <summary>单条消息上限（源：defaultWebSocketMaximumMessageSize = 4 MB）。</summary>
    public const int MaxWebSocketMessageBytes = 4 * 1024 * 1024;

    private const int ChunkSize = 16 * 1024;

    public static async Task<WebSocketStreamMessage> ReceiveAsync(
        WebSocket socket,
        int maximumMessageBytes = MaxWebSocketMessageBytes,
        CancellationToken cancellationToken = default)
    {
        using var accumulated = new MemoryStream();
        var chunk = new byte[ChunkSize];
        WebSocketReceiveResult result;
        while (true)
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // 对齐 URLSession：收到对端 close 帧后自动完成关闭握手，再以异常向上报断线；
                // 否则对端的 CloseAsync 会一直等不到回应而挂起
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                throw MihomoApiException.StreamClosed(
                    (int?)result.CloseStatus, result.CloseStatusDescription);
            }

            if (result.Count > 0)
            {
                accumulated.Write(chunk, 0, result.Count);
                if (accumulated.Length > maximumMessageBytes)
                {
                    throw MihomoApiException.MessageTooLarge();
                }
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        if (result.MessageType == WebSocketMessageType.Text)
        {
            return WebSocketStreamMessage.FromText(Encoding.UTF8.GetString(accumulated.ToArray()));
        }

        return WebSocketStreamMessage.FromBinary(accumulated.ToArray());
    }
}
