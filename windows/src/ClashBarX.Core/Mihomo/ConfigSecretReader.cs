namespace ClashBarX.Core.Mihomo;

/// <summary>
/// 从 mihomo 配置文本读顶层 secret 标量（源：macOS parseYAMLScalarValue(forKey:"secret")）。
/// 本机控制器鉴权用：配置带 secret 而客户端不带时全部 401（真机实测 BOSS 生产配置即带 secret）。
/// 只认顶格（无前置空白）的 `secret:` 行；支持单/双引号与行尾注释；缺失或空值返回 null。
/// </summary>
public static class ConfigSecretReader
{
    private const string Key = "secret:";

    public static string? TryRead(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == ' ' || line[0] == '\t' || line[0] == '#' || line[0] == '-')
            {
                continue;
            }

            if (!line.StartsWith(Key, StringComparison.Ordinal))
            {
                continue;
            }

            return ParseScalar(line[Key.Length..].Trim());
        }

        return null;
    }

    private static string? ParseScalar(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (value[0] is '"' or '\'')
        {
            char quote = value[0];
            int end = value.IndexOf(quote, 1);
            string inner = end > 0 ? value[1..end] : value[1..];
            return inner.Length == 0 ? null : inner;
        }

        int comment = value.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0)
        {
            value = value[..comment];
        }

        value = value.Trim();
        return value.Length == 0 ? null : value;
    }
}
