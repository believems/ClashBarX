using System.Net;

namespace ClashBarX.Core.ProxyGuard;

/// <summary>
/// 连通性探测（源：AppViewModel+ProxyGuard.swift 的 probeConnectivity）：
/// 经本地混合端口对 204 URL 发 HEAD 请求，非 2xx / 超时 / 传输错误均视为不可用。
/// 每次探测用独立 HttpClient（对齐 Swift 的 ephemeral session + finishTasksAndInvalidate）。
/// </summary>
public static class ProxyGuardProbe
{
    /// <summary>请求超时 6 秒、资源总超时 8 秒（源：probeTimeout = 6，resource = +2）。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan ResourceTimeout = TimeSpan.FromSeconds(8);

    public static async Task<bool> ProbeAsync(
        string probeUrl,
        int proxyPort,
        HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        if (proxyPort <= 0 || !Uri.TryCreate(probeUrl, UriKind.Absolute, out Uri? url))
        {
            return false;
        }

        // HEAD 经 127.0.0.1:{mixedPort} 代理发出（源：connectionProxyDictionary）
        HttpMessageHandler effectiveHandler = handler ?? new SocketsHttpHandler
        {
            Proxy = new WebProxy("127.0.0.1", proxyPort),
            UseProxy = true,
        };

        try
        {
            using var client = new HttpClient(effectiveHandler, disposeHandler: handler is null)
            {
                Timeout = ResourceTimeout,
            };

            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(RequestTimeout);

            try
            {
                using HttpResponseMessage response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                    .ConfigureAwait(false);
                return (int)response.StatusCode is >= 200 and < 300;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;   // 请求超时视为不可用（源：非 2xx / 超时均失败）
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
