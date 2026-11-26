using System.Text.Json;
using System.Text.Json.Serialization;
using ClashBarX.Core.Json;

namespace ClashBarX.Core.Models;

/// <summary>内存快照（源：Runtime.swift 的 MemorySnapshot，inuse 缺失按 0）。</summary>
[JsonConverter(typeof(MemorySnapshotConverter))]
public sealed record MemorySnapshot(long Inuse)
{
    public static MemorySnapshot FromJson(JsonElement element) =>
        new(FlexibleJson.Int64OrDefault(element, "inuse"));
}

public sealed class MemorySnapshotConverter : JsonConverter<MemorySnapshot>
{
    public override MemorySnapshot? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return MemorySnapshot.FromJson(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, MemorySnapshot value, JsonSerializerOptions options) =>
        throw new NotSupportedException("仅反序列化");
}
