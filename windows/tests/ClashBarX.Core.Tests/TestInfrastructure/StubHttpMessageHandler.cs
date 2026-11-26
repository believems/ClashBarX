using System.Net;

namespace ClashBarX.Core.Tests.TestInfrastructure;

/// <summary>记录到的一次 HTTP 请求（请求发出时的快照）。</summary>
public sealed record RecordedHttpRequest(
    HttpMethod Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    byte[]? Body);

/// <summary>
/// 脚本化 HttpMessageHandler：按序返回预设响应或抛预设异常；脚本耗尽后抛错，
/// 用于发现「意外的多余请求」。
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    /// <summary>
    /// 一步脚本：Response、Error、Handler 三选一。
    /// </summary>
    public sealed record ScriptStep(
        HttpResponseMessage? Response = null,
        Exception? Error = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Handler = null);

    private readonly Queue<ScriptStep> _steps = new();
    private readonly object _gate = new();

    public List<RecordedHttpRequest> Requests { get; } = new();

    /// <summary>脚本耗尽后无限重复最后一步（多次请求场景，如重复导入）。</summary>
    public bool RepeatLastStep { get; set; }

    private ScriptStep? _lastStep;

    public static StubHttpMessageHandler RespondJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new StubHttpMessageHandler(new ScriptStep(Response: Json(json, statusCode)));
    }

    public static StubHttpMessageHandler Script(params ScriptStep[] steps) => new(steps);

    public StubHttpMessageHandler(params ScriptStep[] steps)
    {
        foreach (var step in steps)
        {
            _steps.Enqueue(step);
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return new HttpResponseMessage(statusCode) { Content = content };
    }

    public static HttpResponseMessage Text(string body, HttpStatusCode statusCode) =>
        new(statusCode) { Content = new StringContent(body) };

    /// <summary>模拟连接失败（可重试的传输层错误）。</summary>
    public static Exception ConnectionError(string message = "connection refused") =>
        new HttpRequestException(HttpRequestError.ConnectionError, message);

    /// <summary>模拟请求超时（SendAsync 被内部 CTS 取消时的异常形态）。</summary>
    public static Exception SimulatedTimeout() => new TaskCanceledException("simulated timeout");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        byte[]? body = null;
        if (request.Content != null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        lock (_gate)
        {
            Requests.Add(new RecordedHttpRequest(
                request.Method,
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                headers,
                body));
        }

        ScriptStep step;
        lock (_gate)
        {
            if (_steps.Count == 0)
            {
                if (RepeatLastStep && _lastStep != null)
                {
                    step = _lastStep;
                }
                else
                {
                    throw new InvalidOperationException("测试脚本已耗尽，收到多余的请求");
                }
            }
            else
            {
                step = _steps.Dequeue();
                _lastStep = step;
            }
        }

        if (step.Error != null)
        {
            throw step.Error;
        }

        if (step.Handler != null)
        {
            return await step.Handler(request, cancellationToken);
        }

        return step.Response!;
    }
}
