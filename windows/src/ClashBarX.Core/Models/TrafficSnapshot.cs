using System.Text.Json;
using System.Text.Json.Serialization;
using ClashBarX.Core.Json;

namespace ClashBarX.Core.Models;

/// <summary>
/// 流量快照（源：Runtime.swift 的 TrafficSnapshot）。up/down 缺失按 0；
/// 总量字段按 upTotal/uptotal/uploadTotal 与 downTotal/downtotal/downloadTotal 顺序回退。
/// </summary>
[JsonConverter(typeof(TrafficSnapshotConverter))]
public sealed record TrafficSnapshot(long Up, long Down, long? UpTotal, long? DownTotal)
{
    public static TrafficSnapshot FromJson(JsonElement element)
    {
        long up = FlexibleJson.Int64OrDefault(element, "up");
        long down = FlexibleJson.Int64OrDefault(element, "down");
        long? upTotal = FlexibleJson.Int64OrNull(element, "upTotal")
            ?? FlexibleJson.Int64OrNull(element, "uptotal")
            ?? FlexibleJson.Int64OrNull(element, "uploadTotal");
        long? downTotal = FlexibleJson.Int64OrNull(element, "downTotal")
            ?? FlexibleJson.Int64OrNull(element, "downtotal")
            ?? FlexibleJson.Int64OrNull(element, "downloadTotal");
        return new TrafficSnapshot(up, down, upTotal, downTotal);
    }
}

public sealed class TrafficSnapshotConverter : JsonConverter<TrafficSnapshot>
{
    public override TrafficSnapshot? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return TrafficSnapshot.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, TrafficSnapshot value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}
