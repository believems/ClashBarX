using System.Text.Json;
using System.Text.Json.Serialization;
using ClashBarX.Core.Json;

namespace ClashBarX.Core.Models;

/// <summary>
/// 连接列表快照（源：Connection.swift 的 ConnectionsSnapshot）。最多保留
/// <see cref="RetainedConnectionLimit"/> 条摘要，但 totalCount 统计全量，供标题栏显示真实总数。
/// </summary>
[JsonConverter(typeof(ConnectionsSnapshotConverter))]
public sealed record ConnectionsSnapshot(
    int TotalCount,
    IReadOnlyList<ConnectionSummary> Connections,
    long? UploadTotal,
    long? DownloadTotal)
{
    public const int RetainedConnectionLimit = 120;

    public static readonly ConnectionsSnapshot Empty = new(0, Array.Empty<ConnectionSummary>(), null, null);

    public static ConnectionsSnapshot FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"连接快照应为对象，实际为 {element.ValueKind}");
        }

        long? uploadTotal = FlexibleJson.Int64OrNull(element, "uploadTotal");
        long? downloadTotal = FlexibleJson.Int64OrNull(element, "downloadTotal");

        if (!FlexibleJson.TryGet(element, "connections", out var array))
        {
            return new ConnectionsSnapshot(0, Array.Empty<ConnectionSummary>(), uploadTotal, downloadTotal);
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"connections 应为数组，实际为 {array.ValueKind}");
        }

        var retained = new List<ConnectionSummary>(capacity: Math.Min(RetainedConnectionLimit, 16));
        int totalCount = 0;
        foreach (var item in array.EnumerateArray())
        {
            totalCount++;
            if (retained.Count < RetainedConnectionLimit)
            {
                retained.Add(ConnectionSummary.FromJson(item, parseStartTimestamp: false));
            }
        }

        return new ConnectionsSnapshot(totalCount, retained, uploadTotal, downloadTotal);
    }
}

public sealed class ConnectionsSnapshotConverter : JsonConverter<ConnectionsSnapshot>
{
    public override ConnectionsSnapshot? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ConnectionsSnapshot.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ConnectionsSnapshot value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}
