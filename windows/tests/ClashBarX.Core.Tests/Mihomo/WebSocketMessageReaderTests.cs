using System.Net.WebSockets;
using System.Text;
using ClashBarX.Core.Mihomo;
using ClashBarX.Core.Tests.TestInfrastructure;

namespace ClashBarX.Core.Tests.Mihomo;

/// <summary>
/// 用 WebSocket.CreateFromStream + 内存双工流在无网络环境下驱动真实 WebSocket 栈，
/// 验证整帧读取、分片重组与 4MB 上限（源：maximumMessageSize = 4MB）。
/// </summary>
public class WebSocketMessageReaderTests
{
    private static (WebSocket Client, WebSocket Peer) CreatePair()
    {
        var (left, right) = DuplexStream.CreatePair();
        WebSocket client = WebSocket.CreateFromStream(left, isServer: false, null, Timeout.InfiniteTimeSpan);
        WebSocket peer = WebSocket.CreateFromStream(right, isServer: true, null, Timeout.InfiniteTimeSpan);
        return (client, peer);
    }

    [Fact]
    public async Task 文本消息整帧读取()
    {
        var (client, peer) = CreatePair();
        using var clientSocket = client;
        using var peerSocket = peer;

        var receiveTask = WebSocketMessageReader.ReceiveAsync(client);
        await peer.SendAsync("{\"up\":1024,\"down\":2048}"u8.ToArray(), WebSocketMessageType.Text, true, default);
        var message = await receiveTask;

        Assert.True(message.IsText);
        Assert.Equal("{\"up\":1024,\"down\":2048}", message.Text);
    }

    [Fact]
    public async Task 分片文本消息被重组为一条()
    {
        var (client, peer) = CreatePair();
        using var clientSocket = client;
        using var peerSocket = peer;

        var receiveTask = WebSocketMessageReader.ReceiveAsync(client);
        await peer.SendAsync("{\"up\":"u8.ToArray(), WebSocketMessageType.Text, false, default);
        await peer.SendAsync("1024}"u8.ToArray(), WebSocketMessageType.Text, true, default);
        var message = await receiveTask;

        Assert.Equal("{\"up\":1024}", message.Text);
    }

    [Fact]
    public async Task 二进制消息原样读取()
    {
        var (client, peer) = CreatePair();
        using var clientSocket = client;
        using var peerSocket = peer;

        var receiveTask = WebSocketMessageReader.ReceiveAsync(client);
        await peer.SendAsync(new byte[] { 1, 2, 3, 255 }, WebSocketMessageType.Binary, true, default);
        var message = await receiveTask;

        Assert.False(message.IsText);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, message.Binary.ToArray());
    }

    [Fact]
    public async Task 中文与emoji消息无损读取()
    {
        var (client, peer) = CreatePair();
        using var clientSocket = client;
        using var peerSocket = peer;

        var receiveTask = WebSocketMessageReader.ReceiveAsync(client);
        await peer.SendAsync(
            Encoding.UTF8.GetBytes("{\"node\":\"香港 01 🇭🇰\"}"), WebSocketMessageType.Text, true, default);
        var message = await receiveTask;

        Assert.Equal("{\"node\":\"香港 01 🇭🇰\"}", message.Text);
    }

    [Fact]
    public async Task 对端关闭时抛出流关闭异常()
    {
        var (client, peer) = CreatePair();
        using var clientSocket = client;

        var receiveTask = WebSocketMessageReader.ReceiveAsync(client);
        await peer.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        using var peerSocket = peer;

        var exception = await Assert.ThrowsAsync<MihomoApiException>(() => receiveTask);
        Assert.Contains("closed by peer", exception.Message);
    }

    [Fact]
    public async Task 消息超过上限立即失败()
    {
        var (client, peer) = CreatePair();
        using var clientSocket = client;
        using var peerSocket = peer;

        var receiveTask = WebSocketMessageReader.ReceiveAsync(client);
        await peer.SendAsync(new byte[WebSocketMessageReader.MaxWebSocketMessageBytes + 1],
            WebSocketMessageType.Binary, true, default);

        await Assert.ThrowsAsync<MihomoApiException>(() => receiveTask);
    }

    [Fact]
    public async Task 恰好等于上限的消息可通过()
    {
        var (client, peer) = CreatePair();
        using var clientSocket = client;
        using var peerSocket = peer;

        var receiveTask = WebSocketMessageReader.ReceiveAsync(client);
        await peer.SendAsync(new byte[WebSocketMessageReader.MaxWebSocketMessageBytes],
            WebSocketMessageType.Binary, true, default);

        var message = await receiveTask;
        Assert.Equal(WebSocketMessageReader.MaxWebSocketMessageBytes, message.Binary.Length);
    }
}
