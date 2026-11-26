using System.Text;
using System.Text.Json;
using ClashBarX.Core.Json;
using ClashBarX.Core.Models;

namespace ClashBarX.Core.Logging;

/// <summary>
/// mihomo 日志帧解码（源：AppViewModel+Streams.swift 的 decodeLogLinePayload）。
/// 三重回退：LogLine（type/payload）→ 历史数组形态 → 裸文本按 info。
/// 等级按 LogsTabView 的 normalizeLogLevel 归一化：error/err→error、warn→warning、其余 info。
/// </summary>
public static class MihomoLogDecoder
{
    private static readonly JsonSerializerOptions Json = JsonSharedOptions.Json;

    /// <summary>解码一帧日志 payload；无法解析出非空消息时返回 false。</summary>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out string level, out string message)
    {
        level = "info";
        message = string.Empty;

        try
        {
            LogLine? line = JsonSerializer.Deserialize<LogLine>(payload, Json);
            // payload trim 后非空才收（源：trimmingCharacters(in: .whitespacesAndNewlines)）
            if (line is { Payload: { } payloadText }
                && payloadText.Trim().Length > 0)
            {
                level = NormalizeLevel(line.Type);
                message = payloadText.Trim();
                return true;
            }
        }
        catch (JsonException)
        {
            // 回退到下一形态
        }

        // 历史数组形态：{"logs":[{type,payload}]} 取首条（源：LogsResponse 分支）
        try
        {
            var reader = new Utf8JsonReader(payload);
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("logs", out JsonElement logs)
                && logs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in logs.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("payload", out JsonElement payloadElement))
                    {
                        continue;
                    }

                    string? text = payloadElement.ValueKind == JsonValueKind.String
                        ? payloadElement.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    level = "info";
                    if (item.TryGetProperty("type", out JsonElement typeElement)
                        && typeElement.ValueKind == JsonValueKind.String)
                    {
                        level = NormalizeLevel(typeElement.GetString());
                    }

                    message = text;
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // 回退到裸文本
        }

        // 裸文本帧（非 JSON 或空结构）按 info 处理
        string rawText = Encoding.UTF8.GetString(payload).Trim();
        if (rawText.Length == 0)
        {
            return false;
        }

        message = rawText;
        return true;
    }

    /// <summary>等级归一化（源：LogsTabView.normalizeLogLevel——子串匹配）。</summary>
    public static string NormalizeLevel(string? rawLevel)
    {
        string lowered = (rawLevel ?? string.Empty).ToLowerInvariant();
        if (lowered.Contains("error") || lowered.Contains("err"))
        {
            return "error";
        }

        if (lowered.Contains("warn"))
        {
            return "warning";
        }

        return "info";
    }
}
