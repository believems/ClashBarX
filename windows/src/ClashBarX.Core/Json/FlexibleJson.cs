using System.Globalization;
using System.Text.Json;

namespace ClashBarX.Core.Json;

/// <summary>
/// 基于 JsonElement 的容错读取辅助（源：KeyedDecodingContainer+FlexibleValues.swift、
/// String+Whitespace.swift）。约定：
/// - 严格读取：键缺失或值为 null 返回 null；值存在但类型不符抛 <see cref="JsonException"/>（对齐 decodeIfPresent）；
/// - Flexible 读取：Int 接受数字或数字字符串；Bool 接受布尔、数字、常见单词；String 接受字符串或数字；
/// - 多键回退：按顺序取第一个「存在且非 null」的键。
/// </summary>
public static class FlexibleJson
{
    public static bool TryGet(JsonElement obj, string key, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out value)
            && value.ValueKind != JsonValueKind.Null)
        {
            return true;
        }

        value = default;
        return false;
    }

    // ---- 严格读取（对齐 decodeIfPresent 的 throw 语义） ----

    public static string? StringOrNull(JsonElement obj, string key) =>
        TryGet(obj, key, out var value) ? RequireString(value, key) : null;

    public static string? TrimmedNonEmptyStringOrNull(JsonElement obj, string key)
    {
        var value = StringOrNull(obj, key);
        return TrimmedNonEmpty(value);
    }

    public static IReadOnlyList<string>? StringArrayOrNull(JsonElement obj, string key)
    {
        if (!TryGet(obj, key, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"属性 {key} 应为数组，实际为 {value.ValueKind}");
        }

        return value.EnumerateArray().Select(item => RequireString(item, key)).ToArray();
    }

    public static int? Int32OrNull(JsonElement obj, string key) =>
        TryGet(obj, key, out var value) ? RequireInt32(value, key) : null;

    public static long? Int64OrNull(JsonElement obj, string key) =>
        TryGet(obj, key, out var value) ? RequireInt64(value, key) : null;

    /// <summary>
    /// Int32 严格多键回退：第一个存在且非 null 的键必须可解析为整数，类型不符抛错；
    /// 键缺失或值为 null 时继续下一键（源：ProviderDetail.ruleCount 的 ruleCount ?? rulesCount ?? count 链）。
    /// </summary>
    public static int? Int32WithStrictFallback(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryGet(obj, key, out _))
            {
                return Int32OrNull(obj, key);
            }
        }

        return null;
    }

    public static bool? BoolOrNull(JsonElement obj, string key) =>
        TryGet(obj, key, out var value) ? RequireBool(value, key) : null;

    /// <summary>缺失/null 返回默认值；数字按 Int64 读取（源：TrafficSnapshot/MemorySnapshot 的 decodeIfPresent ?? 默认值）。</summary>
    public static long Int64OrDefault(JsonElement obj, string key, long defaultValue = 0) =>
        TryGet(obj, key, out var value) ? RequireInt64(value, key) : defaultValue;

    // ---- Flexible 读取（源：decodeFlexibleInt / decodeFlexibleBool / decodeFlexibleString） ----

    public static int? FlexibleInt(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryGet(obj, key, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.Number when value.TryGetInt64(out long longValue):
                    return ClampToInt32(longValue);
                case JsonValueKind.String when int.TryParse(
                    value.GetString()!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed):
                    return parsed;
            }
        }

        return null;
    }

    public static bool? FlexibleBool(JsonElement obj, string key)
    {
        if (!TryGet(obj, key, out var value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number when value.TryGetInt64(out long longValue):
                return longValue != 0;
            case JsonValueKind.String:
                switch (value.GetString()!.Trim().ToLowerInvariant())
                {
                    case "true" or "yes" or "on" or "1":
                        return true;
                    case "false" or "no" or "off" or "0":
                        return false;
                    default:
                        return null;
                }
            default:
                return null;
        }
    }

    /// <summary>依次尝试多个键；字符串会 trim，数字转为十进制文本（源：ConnectionMetadata 的多键回退）。</summary>
    public static string? FlexibleString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryGet(obj, key, out var value))
            {
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                {
                    var trimmed = TrimmedNonEmpty(value.GetString());
                    if (trimmed != null)
                    {
                        return trimmed;
                    }

                    break;
                }
                case JsonValueKind.Number when value.TryGetInt64(out long longValue):
                    return longValue.ToString(CultureInfo.InvariantCulture);
                case JsonValueKind.Number when value.TryGetDouble(out double doubleValue):
                    return doubleValue.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    /// <summary>
    /// Int64 多键回退，类型错误时继续尝试下一键（源：decodeInt64WithFallback 的 try? 语义，
    /// 与 ProviderDetail.ruleCount 的严格链不同）。
    /// </summary>
    public static long? Int64WithFallback(JsonElement obj, string primary, string fallback)
    {
        if (TryGet(obj, primary, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long longValue))
        {
            return longValue;
        }

        if (TryGet(obj, fallback, out value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long fallbackValue))
        {
            return fallbackValue;
        }

        return null;
    }

    /// <summary>
    /// 延迟历史：数组元素取 delay（Flexible Int），收集非空值后保留末尾 limit 个；
    /// 遇到非对象元素立即停止（源：decodeDelayHistory）。
    /// </summary>
    public static int[] DelayHistory(JsonElement obj, string key, int limit = 5)
    {
        if (!TryGet(obj, key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        var samples = new List<int>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                break;
            }

            if (FlexibleInt(entry, "delay") is int delay)
            {
                samples.Add(delay);
            }
        }

        if (limit > 0 && samples.Count > limit)
        {
            return samples.GetRange(samples.Count - limit, limit).ToArray();
        }

        return samples.ToArray();
    }

    /// <summary>
    /// 字符串数组多键回退：数组取非空白元素；单个字符串视为单元素数组；
    /// 全空时继续尝试下一键（源：ConnectionAnyCodingKey.decodeStringArray 的 chains/chain 回退）。
    /// </summary>
    public static IReadOnlyList<string>? FlexibleStringArray(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryGet(obj, key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                var values = value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => TrimmedNonEmpty(item.GetString()))
                    .Where(item => item != null)
                    .Select(item => item!)
                    .ToArray();
                if (values.Length > 0)
                {
                    return values;
                }
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                var single = TrimmedNonEmpty(value.GetString());
                if (single != null)
                {
                    return new[] { single };
                }
            }
        }

        return null;
    }

    public static int? PositiveOrNull(int? value) => value is > 0 ? value : null;

    public static string? TrimmedNonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---- 私有 ----

    private static string RequireString(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"属性 {key} 应为字符串，实际为 {value.ValueKind}");
        }

        return value.GetString()!;
    }

    private static int RequireInt32(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long longValue))
        {
            throw new JsonException($"属性 {key} 应为整数，实际为 {value.ValueKind}");
        }

        return ClampToInt32(longValue);
    }

    private static long RequireInt64(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long longValue))
        {
            throw new JsonException($"属性 {key} 应为整数，实际为 {value.ValueKind}");
        }

        return longValue;
    }

    private static bool RequireBool(JsonElement value, string key)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException($"属性 {key} 应为布尔，实际为 {value.ValueKind}");
        }

        return value.ValueKind == JsonValueKind.True;
    }

    private static int ClampToInt32(long value) => value is >= int.MinValue and <= int.MaxValue
        ? (int)value
        : throw new JsonException($"整数 {value} 超出 Int32 范围");
}
