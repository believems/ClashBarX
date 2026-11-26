using System.Net;
using System.Net.Http;
using System.Text.Json;
using ClashBarX.Core.Json;

namespace ClashBarX.Core.Mihomo;

/// <summary>
/// mihomo External Controller 的 HTTP 客户端（源：MihomoAPIService.swift）。
/// 行为规格：2xx 判定成功；GET 的传输层失败自动重试至多 3 次（间隔 250ms），其余方法不重试；
/// 每个端点独立超时；控制会话与长耗时会话分离（每主机连接数 6/12）。
/// </summary>
public sealed class MihomoApiClient : IMihomoApiClient
{
    /// <summary>重试间隔（源：send 循环里的 250ms Task.sleep）。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _credentialsLock = new();
    private string _controller;
    private string? _secret;

    private readonly HttpClient _controlClient;
    private readonly HttpClient _longRunningClient;
    private readonly HttpMessageHandler? _ownedControlHandler;
    private readonly HttpMessageHandler? _ownedLongRunningHandler;

    /// <summary>
    /// 构造客户端。<paramref name="handler"/> 供测试注入桩 handler；生产缺省按会话档位
    /// 自建 SocketsHttpHandler（无 cookie、不缓存、环回地址绕过代理）。
    /// </summary>
    public MihomoApiClient(string controller, string? secret = null, HttpMessageHandler? handler = null)
    {
        _controller = controller;
        _secret = secret;

        if (handler != null)
        {
            _controlClient = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            _longRunningClient = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }
        else
        {
            var controlHandler = CreateDefaultHandler(maxConnectionsPerServer: 6);
            var longRunningHandler = CreateDefaultHandler(maxConnectionsPerServer: 12);
            _controlClient = new HttpClient(controlHandler) { Timeout = Timeout.InfiniteTimeSpan };
            _longRunningClient = new HttpClient(longRunningHandler) { Timeout = Timeout.InfiniteTimeSpan };
            _ownedControlHandler = controlHandler;
            _ownedLongRunningHandler = longRunningHandler;
        }
    }

    public string Controller
    {
        get { lock (_credentialsLock) return _controller; }
    }

    public void UpdateCredentials(string controller, string? secret)
    {
        lock (_credentialsLock)
        {
            _controller = controller;
            _secret = secret;
        }
    }

    public async Task<T> RequestAsync<T>(MihomoEndpoint endpoint, CancellationToken cancellationToken = default)
        where T : class
    {
        byte[] data = await SendAsync(endpoint, cancellationToken).ConfigureAwait(false);
        T? result = JsonSerializer.Deserialize<T>(data, JsonSharedOptions.Json);
        return result ?? throw MihomoApiException.InvalidResponse();
    }

    public Task RequestNoResponseAsync(MihomoEndpoint endpoint, CancellationToken cancellationToken = default) =>
        SendAsync(endpoint, cancellationToken);

    public IWebSocketConnection CreateWebSocketConnection(MihomoEndpoint endpoint)
    {
        MihomoWebSocketRequest request = BuildWebSocketRequest(endpoint);
        return new ClientWebSocketConnection(request);
    }

    public void Dispose()
    {
        _controlClient.Dispose();
        _longRunningClient.Dispose();
        _ownedControlHandler?.Dispose();
        _ownedLongRunningHandler?.Dispose();
    }

    // ---- 发送与重试 ----

    internal async Task<byte[]> SendAsync(MihomoEndpoint endpoint, CancellationToken externalCancellationToken)
    {
        HttpClient client = endpoint.UsesLongRunningSession ? _longRunningClient : _controlClient;
        // 源：maxAttempts = method == .get ? 3 : 1
        int maxAttempts = endpoint.Method == MihomoHttpMethod.Get ? 3 : 1;
        Exception? lastError = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return await SendOnceAsync(client, endpoint, externalCancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lastError = error;
                if (!ShouldRetryInSend(error, endpoint, attempt) || attempt == maxAttempts - 1)
                {
                    throw;
                }

                await Task.Delay(RetryInterval, externalCancellationToken).ConfigureAwait(false);
            }
        }

        throw lastError ?? MihomoApiException.InvalidResponse();
    }

    private async Task<byte[]> SendOnceAsync(
        HttpClient client, MihomoEndpoint endpoint, CancellationToken externalCancellationToken)
    {
        (Uri uri, string? authorization) = BuildHttpTarget(endpoint);
        using var request = new HttpRequestMessage(endpoint.Method.ToHttpMethod(), uri);

        // 源：所有请求统一带 Content-Type: application/json（含无 body 的请求）。
        // .NET 的请求头集合会丢弃内容头，因此无 body 时也挂空内容（Content-Length: 0）。
        request.Content = new ByteArrayContent(endpoint.Body ?? Array.Empty<byte>());
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        if (authorization != null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
        timeoutCts.CancelAfter(endpoint.Timeout);
        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(request, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 源：String(data:,utf8) ?? "unknown error"。读取中断链则向上传播（外层按 networkConnectionLost 重试）
                string message = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                throw MihomoApiException.HttpError((int)response.StatusCode, message);
            }

            return await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!externalCancellationToken.IsCancellationRequested)
        {
            // 端点超时（外部取消不在此列，会原样向上传播）
            throw new TimeoutException(
                $"mihomo request timed out ({endpoint.Timeout.TotalSeconds:0.##}s): {endpoint.Method} {uri.PathAndQuery}");
        }
    }

    internal static bool ShouldRetryInSend(Exception error, MihomoEndpoint endpoint, int attempt)
    {
        if (!ShouldRetryTransport(error))
        {
            return false;
        }

        // 源：超时仅当端点超时 < 5s 且是第一次尝试时才重试
        if (error is TimeoutException)
        {
            return endpoint.Timeout < TimeSpan.FromSeconds(5) && attempt == 0;
        }

        return true;
    }

    /// <summary>
    /// 可重试的传输层错误（源：shouldRetry 的 URLError code 白名单）：
    /// 连接失败 / DNS 解析失败 / 协议层中断 / 响应中途断链 / body 读取断链 / 超时。
    /// 业务错误（非 2xx）与非传输异常一律不可重试。
    /// </summary>
    internal static bool ShouldRetryTransport(Exception error) => error switch
    {
        TimeoutException => true,
        IOException => true,
        HttpRequestException httpRequestException => httpRequestException.HttpRequestError
            is HttpRequestError.ConnectionError
            or HttpRequestError.NameResolutionError
            or HttpRequestError.HttpProtocolError
            or HttpRequestError.ResponseEnded,
        _ => false,
    };

    private (Uri Uri, string? Authorization) BuildHttpTarget(MihomoEndpoint endpoint)
    {
        (string controller, string? secret) = SnapshotCredentials();
        string url = MihomoUrlBuilder.BuildHttpUrl(controller, endpoint);
        return (ParseUri(url), BuildAuthorizationHeader(secret));
    }

    internal MihomoWebSocketRequest BuildWebSocketRequest(MihomoEndpoint endpoint)
    {
        (string controller, string? secret) = SnapshotCredentials();
        string url = MihomoUrlBuilder.BuildWebSocketUrl(controller, endpoint);
        Uri uri = ParseUri(url);

        var headers = new Dictionary<string, string>();
        string? authorization = BuildAuthorizationHeader(secret);
        if (authorization != null)
        {
            headers["Authorization"] = authorization;
        }

        return new MihomoWebSocketRequest(uri, headers);
    }

    private (string Controller, string? Secret) SnapshotCredentials()
    {
        lock (_credentialsLock)
        {
            return (_controller, _secret);
        }
    }

    private static Uri ParseUri(string url)
    {
        try
        {
            return new Uri(url, UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            throw MihomoApiException.InvalidUrl();
        }
    }

    private static string? BuildAuthorizationHeader(string? secret) =>
        string.IsNullOrEmpty(secret) ? null : $"Bearer {secret}";

    private static SocketsHttpHandler CreateDefaultHandler(int maxConnectionsPerServer) => new()
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxConnectionsPerServer = maxConnectionsPerServer,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        Proxy = new LoopbackBypassWebProxy(),
        UseProxy = true,
    };
}
