using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClashBarX.Core.Json;

namespace ClashBarX.Core.Models;

/// <summary>
/// 单条连接摘要（源：Connection.swift 的 ConnectionSummary）。
/// rulePayload 与 chains 有多键回退；metadata 内部字段亦有回退（见 <see cref="ConnectionMetadata"/>）。
/// </summary>
[JsonConverter(typeof(ConnectionSummaryConverter))]
public sealed record ConnectionSummary(
    string Id,
    long? Upload,
    long? Download,
    string? Start,
    double? StartTimestamp,
    string? Rule,
    string? RulePayload,
    IReadOnlyList<string>? Chains,
    ConnectionMetadata? Metadata)
{
    /// <summary>解析 ISO8601 的 start 字段为 epoch 秒；失败或为空返回 null（源：parseTimestamp）。</summary>
    public static double? ParseTimestamp(string? start)
    {
        var trimmed = FlexibleJson.TrimmedNonEmpty(start);
        if (trimmed == null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToUnixTimeMilliseconds() / 1000.0
            : null;
    }

    /// <summary>
    /// 从 JsonElement 构造。<paramref name="parseStartTimestamp"/> 为 false 时跳过时间戳解析
    /// （源：ConnectionsSnapshot 解码时传 false 的性能约定）。
    /// </summary>
    public static ConnectionSummary FromJson(JsonElement element, bool parseStartTimestamp = true)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"连接摘要应为对象，实际为 {element.ValueKind}");
        }

        string id = FlexibleJson.StringOrNull(element, "id")
            ?? throw new JsonException("连接摘要缺少 id");

        return new ConnectionSummary(
            Id: id,
            Upload: FlexibleJson.Int64OrNull(element, "upload"),
            Download: FlexibleJson.Int64OrNull(element, "download"),
            Start: FlexibleJson.StringOrNull(element, "start"),
            StartTimestamp: parseStartTimestamp ? ParseTimestamp(FlexibleJson.StringOrNull(element, "start")) : null,
            Rule: FlexibleJson.StringOrNull(element, "rule"),
            RulePayload: FlexibleJson.FlexibleString(element, "rulePayload", "rule_payload", "rulepayload", "payload"),
            Chains: FlexibleJson.FlexibleStringArray(element, "chains", "chain"),
            Metadata: FlexibleJson.TryGet(element, "metadata", out var metadata)
                ? ConnectionMetadata.FromJson(metadata)
                : null);
    }
}

public sealed class ConnectionSummaryConverter : JsonConverter<ConnectionSummary>
{
    public override ConnectionSummary? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ConnectionSummary.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ConnectionSummary value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}

/// <summary>
/// 连接元数据（源：Connection.swift 的 ConnectionMetadata）。主键缺失或空白时按备用键回退。
/// </summary>
[JsonConverter(typeof(ConnectionMetadataConverter))]
public sealed record ConnectionMetadata(
    string? Network,
    string? SourceIP,
    int? SourcePort,
    string? DestinationIP,
    string? Host,
    string? SniffHost,
    string? Process,
    string? ProcessPath)
{
    public static ConnectionMetadata FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"连接元数据应为对象，实际为 {element.ValueKind}");
        }

        return new ConnectionMetadata(
            Network: FlexibleJson.TrimmedNonEmptyStringOrNull(element, "network")
                ?? FlexibleJson.FlexibleString(element, "networkType", "type"),
            SourceIP: FlexibleJson.TrimmedNonEmptyStringOrNull(element, "sourceIP")
                ?? FlexibleJson.FlexibleString(element, "source_ip", "source", "clientIP"),
            SourcePort: FlexibleJson.FlexibleInt(element, "sourcePort", "source_port"),
            DestinationIP: FlexibleJson.TrimmedNonEmptyStringOrNull(element, "destinationIP")
                ?? FlexibleJson.FlexibleString(element, "destination_ip", "destination", "remoteIP", "remoteAddress"),
            Host: FlexibleJson.TrimmedNonEmptyStringOrNull(element, "host")
                ?? FlexibleJson.FlexibleString(element, "destinationHost", "remoteHost", "addr"),
            SniffHost: FlexibleJson.TrimmedNonEmptyStringOrNull(element, "sniffHost"),
            Process: FlexibleJson.TrimmedNonEmptyStringOrNull(element, "process"),
            ProcessPath: FlexibleJson.TrimmedNonEmptyStringOrNull(element, "processPath"));
    }
}

public sealed class ConnectionMetadataConverter : JsonConverter<ConnectionMetadata>
{
    public override ConnectionMetadata? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ConnectionMetadata.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ConnectionMetadata value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}
