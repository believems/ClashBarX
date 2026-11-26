using System.Text.Json;
using System.Text.Json.Serialization;
using ClashBarX.Core.Json;

namespace ClashBarX.Core.Models;

/// <summary>
/// 规则列表（源：Rules.swift 的 RulesSummary）。最多保留 <see cref="RetainedRuleLimit"/> 条，
/// totalCount 统计全量。
/// </summary>
[JsonConverter(typeof(RulesSummaryConverter))]
public sealed record RulesSummary(int TotalCount, IReadOnlyList<RuleItem> Rules)
{
    public const int RetainedRuleLimit = 20000;

    public static readonly RulesSummary Empty = new(0, Array.Empty<RuleItem>());

    public static RulesSummary FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"规则列表应为对象，实际为 {element.ValueKind}");
        }

        if (!FlexibleJson.TryGet(element, "rules", out var array))
        {
            return Empty;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"rules 应为数组，实际为 {array.ValueKind}");
        }

        var retained = new List<RuleItem>(capacity: 16);
        int totalCount = 0;
        foreach (var item in array.EnumerateArray())
        {
            totalCount++;
            if (retained.Count < RetainedRuleLimit)
            {
                retained.Add(RuleItem.FromJson(item));
            }
        }

        return new RulesSummary(totalCount, retained);
    }
}

/// <summary>单条规则（源：Rules.swift 的 RuleItem）。</summary>
[JsonConverter(typeof(RuleItemConverter))]
public sealed record RuleItem(string? Type, string? Payload, string? Proxy)
{
    public static RuleItem FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"规则项应为对象，实际为 {element.ValueKind}");
        }

        return new RuleItem(
            Type: FlexibleJson.StringOrNull(element, "type"),
            Payload: FlexibleJson.StringOrNull(element, "payload"),
            Proxy: FlexibleJson.StringOrNull(element, "proxy"));
    }
}

public sealed class RulesSummaryConverter : JsonConverter<RulesSummary>
{
    public override RulesSummary? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return RulesSummary.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, RulesSummary value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}

public sealed class RuleItemConverter : JsonConverter<RuleItem>
{
    public override RuleItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return RuleItem.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, RuleItem value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}
