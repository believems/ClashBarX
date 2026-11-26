namespace ClashBarX.Core.Mihomo;

/// <summary>
/// WebSocket 握手请求描述：目标 URL 与请求头（源：MihomoAPIService 的 buildWebSocketRequest）。
/// 由 MihomoApiClient 的纯逻辑生成，ClientWebSocketConnection 负责机械应用。
/// </summary>
public sealed record MihomoWebSocketRequest(Uri Uri, IReadOnlyDictionary<string, string> Headers);
