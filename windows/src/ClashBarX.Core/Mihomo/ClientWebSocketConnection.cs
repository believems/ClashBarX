using System.Net.WebSockets;

namespace ClashBarX.Core.Mihomo;

/// <summary>
/// 基于 ClientWebSocket 的连接实现（源：makeWebSocketTask + resume）。
/// 请求头与目标 URL 来自 <see cref="MihomoWebSocketRequest"/>；对齐 URLSession 默认不主动 ping。
/// </summary>
public sealed class ClientWebSocketConnection : IWebSocketConnection
{
    private readonly MihomoWebSocketRequest _request;
    private readonly Func<ClientWebSocket>? _socketFactory;
    private ClientWebSocket? _socket;

    public ClientWebSocketConnection(MihomoWebSocketRequest request, Func<ClientWebSocket>? socketFactory = null)
    {
        _request = request;
        _socketFactory = socketFactory;
    }

    private ClientWebSocket Socket =>
        _socket ?? throw new InvalidOperationException("WebSocket 尚未连接");

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ClientWebSocket socket = _socketFactory?.Invoke() ?? new ClientWebSocket();
        try
        {
            foreach (KeyValuePair<string, string> header in _request.Headers)
            {
                socket.Options.SetRequestHeader(header.Key, header.Value);
            }

            // 源：URLSessionConfiguration.webSocketPingFrameInterval 默认 nil（不主动 ping）
            socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;

            await socket.ConnectAsync(_request.Uri, cancellationToken);
            _socket = socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Task<WebSocketStreamMessage> ReceiveAsync(CancellationToken cancellationToken) =>
        WebSocketMessageReader.ReceiveAsync(Socket, cancellationToken: cancellationToken);

    /// <summary>立即中止并释放连接（对齐 cancel(with: .goingAway) 的即时断开语义，不做关闭握手）。</summary>
    public ValueTask DisposeAsync()
    {
        ClientWebSocket? socket = _socket;
        _socket = null;
        if (socket != null)
        {
            socket.Abort();
            socket.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
