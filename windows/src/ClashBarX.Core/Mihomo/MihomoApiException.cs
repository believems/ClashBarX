namespace ClashBarX.Core.Mihomo;

/// <summary>
/// mihomo API 层错误（源：MihomoAPIService.swift 的 APIError）。
/// 文案保持与 macOS 版一致，便于问题对照。
/// </summary>
public sealed class MihomoApiException : Exception
{
    /// <summary>非 2xx 时的状态码；其余错误为 null。</summary>
    public int? StatusCode { get; }

    /// <summary>非 2xx 时的响应正文文本。</summary>
    public string? ResponseBody { get; }

    private MihomoApiException(string message, int? statusCode, string? responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>Controller URL 无法解析（对应 APIError.invalidURL）。</summary>
    public static MihomoApiException InvalidUrl() => new("Controller URL is invalid", null, null);

    /// <summary>响应不是合法的 HTTP 应答（对应 APIError.invalidResponse）。</summary>
    public static MihomoApiException InvalidResponse() => new("Invalid API response", null, null);

    /// <summary>非 2xx 状态码（对应 APIError.statusCode）。</summary>
    public static MihomoApiException HttpError(int statusCode, string body) =>
        new($"API request failed ({statusCode}): {body}", statusCode, body);

    /// <summary>WebSocket 单条消息超过 4 MB 协议上限（对应 maximumMessageSize 超限）。</summary>
    public static MihomoApiException MessageTooLarge() =>
        new("WebSocket message exceeded the 4 MB limit", null, null);

    /// <summary>WebSocket 被对端关闭（对应 receive() 在收到 close 帧后抛错）。</summary>
    public static MihomoApiException StreamClosed(int? closeStatus, string? closeDescription) =>
        new($"WebSocket closed by peer (status: {closeStatus?.ToString() ?? "无"}, reason: {closeDescription ?? "无"})",
            null, null);
}
