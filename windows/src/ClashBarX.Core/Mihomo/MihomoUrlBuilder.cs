using System.Globalization;
using System.Text;

namespace ClashBarX.Core.Mihomo;

/// <summary>
/// mihomo 控制器 URL 构建（源：MihomoAPIService.swift 的 endpointURL / normalizedControllerAddress /
/// urlPathSegmentEscaped）。
/// </summary>
public static class MihomoUrlBuilder
{
    /// <summary>
    /// 归一化控制器地址：HTTP 请求缺省补 http://；WebSocket 请求把 http(s) 前缀替换为 ws(s)，
    /// 均不做大小写折叠（对齐 Swift hasPrefix 的精确匹配语义）。
    /// </summary>
    public static string NormalizeControllerAddress(string controller, bool forWebSocket)
    {
        bool hasHttpScheme = controller.StartsWith("http://", StringComparison.Ordinal)
            || controller.StartsWith("https://", StringComparison.Ordinal);

        if (!forWebSocket)
        {
            return hasHttpScheme ? controller : $"http://{controller}";
        }

        if (controller.StartsWith("ws://", StringComparison.Ordinal)
            || controller.StartsWith("wss://", StringComparison.Ordinal))
        {
            return controller;
        }

        if (hasHttpScheme)
        {
            // 全文替换，对齐 Swift replacingOccurrences 的行为
            return controller.Replace("https://", "wss://", StringComparison.Ordinal)
                .Replace("http://", "ws://", StringComparison.Ordinal);
        }

        return $"ws://{controller}";
    }

    /// <summary>
    /// 路径段转义（源：String.urlPathSegmentEscaped = urlPathAllowed 去掉 "/"）。
    /// 保留 RFC 3986 路径合法字符（unreserved / sub-delims / ":" / "@" / "[]"），
    /// 其余按 UTF-8 逐字节百分号编码（大写十六进制），"/" 编码为 %2F。
    /// </summary>
    public static string EscapePathSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return string.Empty;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(segment);
        var builder = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            if (IsAllowedPathByte(b))
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>查询值转义。对 &amp;、= 等结构字符做百分号编码，服务端解码语义与 URLComponents 一致。</summary>
    public static string EscapeQueryValue(string value) => Uri.EscapeDataString(value);

    /// <summary>
    /// 构建完整 HTTP URL：控制器地址中的路径段被端点路径整体替换；端点带 query 时整体覆盖，
    /// 否则保留控制器地址原有的 query（源：endpointURL 的 components.path / queryItems 行为）。
    /// </summary>
    public static string BuildHttpUrl(string controller, MihomoEndpoint endpoint) =>
        BuildUrl(NormalizeControllerAddress(controller, forWebSocket: false), endpoint);

    /// <summary>构建 WebSocket URL（scheme 已换成 ws/wss，path 与 query 规则同 HTTP）。</summary>
    public static string BuildWebSocketUrl(string controller, MihomoEndpoint endpoint) =>
        BuildUrl(NormalizeControllerAddress(controller, forWebSocket: true), endpoint);

    private static string BuildUrl(string normalizedController, MihomoEndpoint endpoint)
    {
        // scheme://authority 之后的部分整体让位给端点路径；原 query 在端点无 query 时保留
        int schemeEnd = normalizedController.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            throw MihomoApiException.InvalidUrl();
        }

        int authorityStart = schemeEnd + 3;
        int pathStart = normalizedController.IndexOf('/', authorityStart);
        if (pathStart < 0)
        {
            pathStart = normalizedController.Length;
        }

        string origin = normalizedController[..pathStart];
        string url = origin + endpoint.Path;

        if (endpoint.QueryItems.Count > 0)
        {
            string query = string.Join("&",
                endpoint.QueryItems.Select(pair => $"{EscapeQueryValue(pair.Key)}={EscapeQueryValue(pair.Value)}"));
            url += "?" + query;
        }
        else
        {
            string remainder = normalizedController[pathStart..];
            int queryStart = remainder.IndexOf('?', StringComparison.Ordinal);
            if (queryStart >= 0)
            {
                url += remainder[queryStart..];
            }
        }

        return url;
    }

    private static bool IsAllowedPathByte(byte b) => b switch
    {
        >= (byte)'A' and <= (byte)'Z' => true,
        >= (byte)'a' and <= (byte)'z' => true,
        >= (byte)'0' and <= (byte)'9' => true,
        (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' => true,
        (byte)'!' or (byte)'$' or (byte)'&' or (byte)'\'' or (byte)'(' or (byte)')' => true,
        (byte)'*' or (byte)'+' or (byte)',' or (byte)';' or (byte)'=' => true,
        (byte)':' or (byte)'@' or (byte)'[' or (byte)']' => true,
        _ => false,
    };
}
