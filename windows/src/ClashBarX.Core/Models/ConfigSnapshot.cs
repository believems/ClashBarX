using System.Text.Json;
using System.Text.Json.Serialization;
using ClashBarX.Core.Json;

namespace ClashBarX.Core.Models;

/// <summary>
/// 运行配置快照（源：Proxy.swift 的 ConfigSnapshot，字段名对齐 mihomo 原始 kebab-case JSON）。
/// 布尔与整数字段为严格类型；仅 tun 的 enable/stack 走 Flexible 解析。
/// </summary>
public sealed record ConfigSnapshot(
    [property: JsonPropertyName("allow-lan")] bool? AllowLan,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("log-level")] string? LogLevel,
    [property: JsonPropertyName("ipv6")] bool? Ipv6,
    [property: JsonPropertyName("tcp-concurrent")] bool? TcpConcurrent,
    [property: JsonPropertyName("port")] int? Port,
    [property: JsonPropertyName("socks-port")] int? SocksPort,
    [property: JsonPropertyName("redir-port")] int? RedirPort,
    [property: JsonPropertyName("tproxy-port")] int? TproxyPort,
    [property: JsonPropertyName("mixed-port")] int? MixedPort,
    [property: JsonPropertyName("tun")] ConfigSnapshot.TunConfig? Tun,
    [property: JsonPropertyName("external-controller")] string? ExternalController,
    [property: JsonPropertyName("external-ui-url")] string? ExternalUiUrl,
    [property: JsonPropertyName("external-ui-name")] string? ExternalUiName)
{
    public bool? TunEnabled => Tun?.Enable;

    /// <summary>TUN 配置（enable Flexible Bool、stack Flexible String，源：TunConfig）。</summary>
    [JsonConverter(typeof(TunConfigConverter))]
    public sealed record TunConfig(
        [property: JsonPropertyName("enable")] bool? Enable,
        [property: JsonPropertyName("stack")] string? Stack);
}

public sealed class TunConfigConverter : JsonConverter<ConfigSnapshot.TunConfig>
{
    public override ConfigSnapshot.TunConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"tun 配置应为对象，实际为 {element.ValueKind}");
        }

        return new ConfigSnapshot.TunConfig(
            Enable: FlexibleJson.FlexibleBool(element, "enable"),
            Stack: FlexibleJson.FlexibleString(element, "stack"));
    }

    public override void Write(Utf8JsonWriter writer, ConfigSnapshot.TunConfig value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}
