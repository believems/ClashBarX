namespace ClashBarX.Core.Tun;

/// <summary>
/// 判定 YAML 配置是否在顶层 tun: 块内自行声明了 stack 子键
/// （源：AppViewModel+Tun.swift 的 selectedConfigDeclaresTunStack + childLineExists +
/// topLevelBlockRange + isTopLevelKeyLine + isTopLevelMappingLine 逐行语义）。
/// 声明了 stack 的配置让内核优先用配置值，App 不再注入偏好 stack。
/// </summary>
public static class TunYamlStackScanner
{
    /// <summary>配置文本声明了 tun.stack 时返回 true（CRLF 归一化后按行扫描）。</summary>
    public static bool DeclaresTunStack(string yamlText)
    {
        string[] lines = yamlText.Replace("\r\n", "\n").Split('\n');
        (int start, int end)? tunRange = TopLevelBlockRange(lines, "tun");
        return tunRange is null ? false : ChildLineExists(lines, "stack", tunRange.Value.start, tunRange.Value.end);
    }

    /// <summary>块内缩进行存在 key: 子键（源：childLineExists；跳过空行与注释行）。</summary>
    internal static bool ChildLineExists(string[] lines, string key, int start, int end)
    {
        for (int index = start + 1; index < end; index++)
        {
            string line = lines[index];
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            int leadingSpaces = CountIndent(line);
            if (leadingSpaces == 0)
            {
                continue;
            }

            string content = line[leadingSpaces..].Trim();
            if (content == $"{key}:" || content.StartsWith($"{key}: "))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>顶层 key: 行到下一个顶层映射行的范围（源：topLevelBlockRange）。</summary>
    internal static (int Start, int End)? TopLevelBlockRange(string[] lines, string key)
    {
        int start = -1;
        for (int index = 0; index < lines.Length; index++)
        {
            if (IsTopLevelKeyLine(lines[index], key))
            {
                start = index;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        int end = lines.Length;
        for (int index = start + 1; index < lines.Length; index++)
        {
            if (IsTopLevelMappingLine(lines[index]))
            {
                end = index;
                break;
            }
        }

        return (start, end);
    }

    /// <summary>无缩进且内容为 key: 或 key: 前缀的非注释行（源：isTopLevelKeyLine）。</summary>
    internal static bool IsTopLevelKeyLine(string line, string key)
    {
        if (CountIndent(line) > 0)
        {
            return false;
        }

        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return false;
        }

        return trimmed == $"{key}:" || trimmed.StartsWith($"{key}: ");
    }

    /// <summary>无缩进且含冒号的非注释行（源：isTopLevelMappingLine）。</summary>
    internal static bool IsTopLevelMappingLine(string line)
    {
        if (CountIndent(line) > 0)
        {
            return false;
        }

        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return false;
        }

        return trimmed.Contains(':');
    }

    private static int CountIndent(string line)
    {
        int count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
        {
            count++;
        }

        return count;
    }
}
