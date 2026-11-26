using System.Text.Json;
using System.Text.Json.Serialization;
using ClashBarX.Core.Json;

namespace ClashBarX.Core.Models;

/// <summary>单条内核日志（源：Log.swift 的 LogLine，两字段均可缺省）。</summary>
public sealed record LogLine(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("payload")] string? Payload);

/// <summary>日志批量响应（源：Log.swift 的 LogsResponse）。</summary>
public sealed record LogsResponse(
    [property: JsonPropertyName("logs")] IReadOnlyList<LogLine>? Logs);

/// <summary>
/// 单节点延迟测速结果：优先读 {"delay":N}，否则读取单值对象里的第一个值；
/// 两者都不可用则为 null（源：Log.swift 的 DelayMeasurement）。
/// </summary>
[JsonConverter(typeof(DelayMeasurementConverter))]
public sealed record DelayMeasurement(int? Value)
{
    public static DelayMeasurement FromJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (FlexibleJson.TryGet(element, "delay", out var delay))
            {
                // delay 存在但非数字时与 Swift 一致地抛错；null 则落到单值回退
                return new DelayMeasurement(FlexibleJson.Int32OrNull(element, "delay"));
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out int value))
                {
                    return new DelayMeasurement(value);
                }

                // 与 [String: Int] 解码语义一致：非数字值使整条结果不可用
                return new DelayMeasurement((int?)null);
            }
        }

        return new DelayMeasurement((int?)null);
    }
}

public sealed class DelayMeasurementConverter : JsonConverter<DelayMeasurement>
{
    public override DelayMeasurement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return DelayMeasurement.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, DelayMeasurement value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}

/// <summary>组测速结果：节点名 → 延迟毫秒数；无法解析时为空表（源：Log.swift 的 GroupDelayMeasurement）。</summary>
[JsonConverter(typeof(GroupDelayMeasurementConverter))]
public sealed record GroupDelayMeasurement(IReadOnlyDictionary<string, int> Values)
{
    public static readonly GroupDelayMeasurement Empty = new(new Dictionary<string, int>());

    public static GroupDelayMeasurement FromJson(JsonElement element)
    {
        try
        {
            var values = new Dictionary<string, int>();
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Number
                        || !property.Value.TryGetInt32(out int delay))
                    {
                        // 与 try? decode([String: Int]) 一致：任何不匹配都使整条结果为空表
                        return Empty;
                    }

                    values[property.Name] = delay;
                }
            }

            return new GroupDelayMeasurement(values);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}

public sealed class GroupDelayMeasurementConverter : JsonConverter<GroupDelayMeasurement>
{
    public override GroupDelayMeasurement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return GroupDelayMeasurement.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, GroupDelayMeasurement value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}
