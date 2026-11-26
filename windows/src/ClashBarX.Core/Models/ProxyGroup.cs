using System.Text.Json;
using System.Text.Json.Serialization;
using ClashBarX.Core.Json;

namespace ClashBarX.Core.Models;

/// <summary>代理组集合（源：Proxy.swift 的 ProxyGroupsResponse，键为组名）。</summary>
public sealed record ProxyGroupsResponse(
    [property: JsonPropertyName("proxies")] IReadOnlyDictionary<string, ProxyGroup> Proxies);

/// <summary>
/// 代理组（源：Proxy.swift 的 ProxyGroup）。name 缺省 Unknown；all 缺省为空；
/// testUrl/icon 取 trim 后非空值；timeout 为正整数（Flexible Int）；history 保留末尾 5 条。
/// </summary>
[JsonConverter(typeof(ProxyGroupConverter))]
public sealed record ProxyGroup(
    string Name,
    string? Type,
    string? Now,
    IReadOnlyList<string> All,
    string? TestUrl,
    int? Timeout,
    string? Icon,
    bool? Hidden,
    IReadOnlyList<int> DelayHistory)
{
    public static ProxyGroup FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"代理组应为对象，实际为 {element.ValueKind}");
        }

        return new ProxyGroup(
            Name: FlexibleJson.StringOrNull(element, "name") ?? "Unknown",
            Type: FlexibleJson.StringOrNull(element, "type"),
            Now: FlexibleJson.StringOrNull(element, "now"),
            All: FlexibleJson.StringArrayOrNull(element, "all") ?? Array.Empty<string>(),
            TestUrl: FlexibleJson.TrimmedNonEmptyStringOrNull(element, "testUrl"),
            Timeout: FlexibleJson.PositiveOrNull(FlexibleJson.FlexibleInt(element, "timeout")),
            Icon: FlexibleJson.TrimmedNonEmptyStringOrNull(element, "icon"),
            Hidden: FlexibleJson.BoolOrNull(element, "hidden"),
            DelayHistory: FlexibleJson.DelayHistory(element, "history"));
    }
}

public sealed class ProxyGroupConverter : JsonConverter<ProxyGroup>
{
    public override ProxyGroup? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ProxyGroup.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ProxyGroup value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}
