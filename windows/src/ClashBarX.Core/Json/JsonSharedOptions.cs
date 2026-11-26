using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashBarX.Core.Json;

/// <summary>
/// 全仓共享的 JSON 选项：紧凑输出、保留非 ASCII 原文（对齐 Swift JSONSerialization 的默认输出）、
/// 属性名大小写敏感（对齐 Swift JSONDecoder 默认）。模型一律显式标注 [JsonPropertyName]。
/// </summary>
public static class JsonSharedOptions
{
    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
