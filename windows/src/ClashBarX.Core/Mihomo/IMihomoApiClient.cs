namespace ClashBarX.Core.Mihomo;

/// <summary>
/// mihomo External Controller 客户端（源：MihomoAPITransporting 协议）。
/// 实现须线程安全，凭据可运行时热更新。
/// </summary>
public interface IMihomoApiClient : IDisposable
{
    /// <summary>更新控制器地址与认证密钥（对应 updateCredentials）。</summary>
    void UpdateCredentials(string controller, string? secret);

    /// <summary>发起请求并解码响应体（对应 request）。2xx 且 JSON 解码失败会抛 <see cref="System.Text.Json.JsonException"/>。</summary>
    Task<T> RequestAsync<T>(MihomoEndpoint endpoint, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>发起请求并丢弃响应体（对应 requestNoResponse），用于 204 类端点。</summary>
    Task RequestNoResponseAsync(MihomoEndpoint endpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建一条尚未连接的 WebSocket（对应 makeWebSocketTask）。
    /// URL 非法时抛 <see cref="MihomoApiException"/>；连接失败由调用方在 ConnectAsync 阶段感知。
    /// </summary>
    IWebSocketConnection CreateWebSocketConnection(MihomoEndpoint endpoint);
}
