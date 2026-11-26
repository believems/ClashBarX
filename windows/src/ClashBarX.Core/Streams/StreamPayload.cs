using System.Text;
using ClashBarX.Core.Mihomo;

namespace ClashBarX.Core.Streams;

/// <summary>
/// WS 流 payload 规范化（源：StreamCoordinator.swift 的 normalizeWebSocketPayload）。
/// 文本帧：trim 后为空串 / "null" / "{}" 视为噪声丢弃；有效时广播 trim 后内容的 UTF-8 字节。
/// 二进制帧：仅空载荷视为噪声，其余原样广播（不做 null/{} 检查）。
/// </summary>
public static class StreamPayload
{
    public static ReadOnlyMemory<byte>? Normalize(WebSocketStreamMessage message)
    {
        if (message.Text is string text)
        {
            string trimmed = text.Trim();
            if (trimmed.Length == 0 || trimmed == "null" || trimmed == "{}")
            {
                return null;
            }

            return Encoding.UTF8.GetBytes(trimmed);
        }

        ReadOnlyMemory<byte> binary = message.Binary;
        if (binary.IsEmpty)
        {
            return null;
        }

        return binary;
    }
}
