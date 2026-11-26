using System.Text.Json;
using System.Text.Json.Serialization;
using ClashBarX.Core.Json;

namespace ClashBarX.Core.Models;

/// <summary>订阅源集合（源：Provider.swift 的 ProviderSummary，代理源与规则源共用此形状）。</summary>
public sealed record ProviderSummary(
    [property: JsonPropertyName("providers")] IReadOnlyDictionary<string, ProviderDetail> Providers);

/// <summary>
/// 订阅源详情（源：Provider.swift 的 ProviderDetail）。ruleCount 按 ruleCount/rulesCount/count
/// 严格回退；testUrl 取 trim 后非空值；timeout 为正整数。
/// </summary>
[JsonConverter(typeof(ProviderDetailConverter))]
public sealed record ProviderDetail(
    string? Name,
    string? VehicleType,
    string? TestUrl,
    int? Timeout,
    string? UpdatedAt,
    int? RuleCount,
    ProviderSubscriptionInfo? SubscriptionInfo,
    IReadOnlyList<ProviderProxyNode>? Proxies)
{
    public static ProviderDetail FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"订阅源详情应为对象，实际为 {element.ValueKind}");
        }

        return new ProviderDetail(
            Name: FlexibleJson.StringOrNull(element, "name"),
            VehicleType: FlexibleJson.StringOrNull(element, "vehicleType"),
            TestUrl: FlexibleJson.TrimmedNonEmptyStringOrNull(element, "testUrl"),
            Timeout: FlexibleJson.PositiveOrNull(FlexibleJson.FlexibleInt(element, "timeout")),
            UpdatedAt: FlexibleJson.StringOrNull(element, "updatedAt"),
            RuleCount: FlexibleJson.Int32WithStrictFallback(element, "ruleCount", "rulesCount", "count"),
            SubscriptionInfo: FlexibleJson.TryGet(element, "subscriptionInfo", out var subscriptionInfo)
                ? ProviderSubscriptionInfo.FromJson(subscriptionInfo)
                : null,
            Proxies: FlexibleJson.TryGet(element, "proxies", out var proxies) && proxies.ValueKind == JsonValueKind.Array
                ? proxies.EnumerateArray().Select(ProviderProxyNode.FromJson).ToArray()
                : null);
    }
}

public sealed class ProviderDetailConverter : JsonConverter<ProviderDetail>
{
    public override ProviderDetail? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ProviderDetail.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ProviderDetail value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}

/// <summary>订阅流量信息（源：Provider.swift 的 ProviderSubscriptionInfo，兼容首字母大写的键名回退）。</summary>
[JsonConverter(typeof(ProviderSubscriptionInfoConverter))]
public sealed record ProviderSubscriptionInfo(
    long? Upload,
    long? Download,
    long? Total,
    long? Expire)
{
    public static ProviderSubscriptionInfo FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"订阅信息应为对象，实际为 {element.ValueKind}");
        }

        return new ProviderSubscriptionInfo(
            Upload: FlexibleJson.Int64WithFallback(element, "upload", "Upload"),
            Download: FlexibleJson.Int64WithFallback(element, "download", "Download"),
            Total: FlexibleJson.Int64WithFallback(element, "total", "Total"),
            Expire: FlexibleJson.Int64WithFallback(element, "expire", "Expire"));
    }
}

public sealed class ProviderSubscriptionInfoConverter : JsonConverter<ProviderSubscriptionInfo>
{
    public override ProviderSubscriptionInfo? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ProviderSubscriptionInfo.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ProviderSubscriptionInfo value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}

/// <summary>订阅源中的节点（源：Provider.swift 的 ProviderProxyNode，name 缺省 "-"）。</summary>
[JsonConverter(typeof(ProviderProxyNodeConverter))]
public sealed record ProviderProxyNode(
    string Name,
    string? Type,
    IReadOnlyList<int> DelayHistory)
{
    public static ProviderProxyNode FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"订阅源节点应为对象，实际为 {element.ValueKind}");
        }

        return new ProviderProxyNode(
            Name: FlexibleJson.StringOrNull(element, "name") ?? "-",
            Type: FlexibleJson.StringOrNull(element, "type"),
            DelayHistory: FlexibleJson.DelayHistory(element, "history"));
    }
}

public sealed class ProviderProxyNodeConverter : JsonConverter<ProviderProxyNode>
{
    public override ProviderProxyNode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ProviderProxyNode.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ProviderProxyNode value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}
