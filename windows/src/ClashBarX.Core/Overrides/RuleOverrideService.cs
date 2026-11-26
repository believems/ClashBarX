using System.Text;

namespace ClashBarX.Core.Overrides;

/// <summary>覆写三文件（源：RuleOverrideFile）。</summary>
public enum RuleOverrideFile
{
    Prepend,
    Append,
    RuleProviders,
}

public static class RuleOverrideFileExtensions
{
    public static string FileName(this RuleOverrideFile file) => file switch
    {
        RuleOverrideFile.Prepend => "prepend.list",
        RuleOverrideFile.Append => "append.list",
        RuleOverrideFile.RuleProviders => "rule-providers.yaml",
        _ => throw new ArgumentOutOfRangeException(nameof(file)),
    };

    /// <summary>文件不存在时编辑器显示的注释模板（不落盘，保存时才写；源：file.template）。</summary>
    public static string Template(this RuleOverrideFile file) => file switch
    {
        RuleOverrideFile.Prepend =>
            """
            # 前置规则：插在订阅 rules 的最前面，优先级最高。
            # 一行一条，写法与配置文件 rules 相同，不需要开头的 "- "；以 # 开头的行是注释。
            # 引用的策略组、RULE-SET 规则集必须存在，否则配置校验会失败。
            #
            # DOMAIN-SUFFIX,example.com,DIRECT
            # RULE-SET,my-rules,Proxy

            """,
        RuleOverrideFile.Append =>
            """
            # 追加规则：插在订阅 rules 末尾的 MATCH / FINAL 之前（放在 MATCH 之后永远匹配不到）。
            # 一行一条，写法与配置文件 rules 相同，不需要开头的 "- "；以 # 开头的行是注释。
            #
            # GEOIP,CN,DIRECT

            """,
        RuleOverrideFile.RuleProviders =>
            """
            # 规则集：写 rule-providers: 下面那一层，缩进会自动对齐；与订阅同名时以这里为准。
            # 在前置 / 追加规则里用 RULE-SET,<名称>,<策略> 引用。
            #
            # my-rules:
            #   type: http
            #   behavior: classical
            #   format: yaml
            #   url: https://example.com/rules.yaml
            #   path: ./ruleset/my-rules.yaml
            #   interval: 86400

            """,
        _ => string.Empty,
    };
}

/// <summary>三文件有效条目计数（源：RuleOverrideCounts）。</summary>
public sealed record RuleOverrideCounts(int Prepend, int Append, int RuleProviders)
{
    public bool IsEmpty => Prepend == 0 && Append == 0 && RuleProviders == 0;
}

/// <summary>解析后的覆写内容（源：RuleOverrideContent；ruleProviderLines 已去公共缩进、不含头）。</summary>
public sealed record RuleOverrideContent(
    IReadOnlyList<string> PrependRules,
    IReadOnlyList<string> AppendRules,
    IReadOnlyList<string> RuleProviderLines,
    IReadOnlyList<string> RuleProviderNames)
{
    public RuleOverrideCounts Counts => new(
        PrependRules.Count, AppendRules.Count, RuleProviderNames.Count);
}

/// <summary>覆写解析/拼接失败（源：RuleOverrideError 三态）。</summary>
public sealed class RuleOverrideException : Exception
{
    public RuleOverrideException(string message) : base(message) { }

    public static RuleOverrideException Unreadable(string fileName) =>
        new($"覆写文件无法按 UTF-8 文本读取：{fileName}");

    public static RuleOverrideException UnsupportedFlowStyle(string key) =>
        new($"配置把 `{key}:` 写成行内（[...] / {{...}}）形式；仅支持块式写法");

    public static RuleOverrideException InvalidRuleProviders(int line) =>
        new($"rule-providers.yaml 第 {line} 行不是 `名称:` 条目或悬挂缩进");
}

/// <summary>
/// 规则覆写（源：Services/RuleOverrideService.swift 逐行对照；参考 OpenClash 自定义规则）：
/// overrides/ 下三份纯文本在启动/重载时拼进所选配置，生成 runtime/ 下的运行时配置交给 mihomo，
/// 原配置文件永不被改写。不依赖 YAML 库，按行拼接，仅支持块式 rules / rule-providers。
/// </summary>
public sealed class RuleOverrideService
{
    private readonly string _overridesDirectory;
    private readonly string _runtimeDirectory;

    public RuleOverrideService(string overridesDirectory, string runtimeDirectory)
    {
        _overridesDirectory = overridesDirectory;
        _runtimeDirectory = runtimeDirectory;
    }

    public string FilePath(RuleOverrideFile file) =>
        Path.Combine(_overridesDirectory, file.FileName());

    /// <summary>运行时配置沿用原文件名（校验失败弹窗显示用户认识的名字；源：runtimeConfigURL）。</summary>
    public string RuntimeConfigPath(string originalConfigPath) =>
        Path.Combine(_runtimeDirectory, Path.GetFileName(originalConfigPath));

    public void EnsureOverridesDirectory() => Directory.CreateDirectory(_overridesDirectory);

    /// <summary>编辑器文本：文件不存在时给模板（源：editableText）。</summary>
    public string EditableText(RuleOverrideFile file)
    {
        string path = FilePath(file);
        if (!File.Exists(path))
        {
            return file.Template();
        }

        return ReadTextOrThrow(path, file.FileName());
    }

    /// <summary>原子写（临时文件 + 覆盖移动；源：write 的 .atomic）。</summary>
    public void Write(string text, RuleOverrideFile file)
    {
        EnsureOverridesDirectory();
        string target = FilePath(file);
        string temporary = target + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, target, overwrite: true);
    }

    /// <summary>读取并解析三文件（源：loadContent）。</summary>
    public RuleOverrideContent LoadContent()
    {
        var prepend = ParseRuleList(ReadIfExists(RuleOverrideFile.Prepend));
        var append = ParseRuleList(ReadIfExists(RuleOverrideFile.Append));
        (IReadOnlyList<string> lines, IReadOnlyList<string> names) =
            ParseRuleProviders(ReadIfExists(RuleOverrideFile.RuleProviders));
        return new RuleOverrideContent(prepend, append, lines, names);
    }

    /// <summary>生成运行时配置并写盘，返回路径（源：writeRuntimeConfig；失败抛错不回退原配置）。</summary>
    public string WriteRuntimeConfig(string originalConfigPath, RuleOverrideContent content)
    {
        string fileName = Path.GetFileName(originalConfigPath);
        string raw = ReadTextOrThrow(originalConfigPath, fileName);
        string merged = Apply(content, raw);

        Directory.CreateDirectory(_runtimeDirectory);
        string target = RuntimeConfigPath(originalConfigPath);
        string temporary = target + ".tmp";
        File.WriteAllText(temporary, merged, new UTF8Encoding(false));
        File.Move(temporary, target, overwrite: true);
        return target;
    }

    private string ReadIfExists(RuleOverrideFile file)
    {
        string path = FilePath(file);
        return File.Exists(path) ? ReadTextOrThrow(path, file.FileName()) : string.Empty;
    }

    private static string ReadTextOrThrow(string path, string fileName)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or DecoderFallbackException)
        {
            throw new RuleOverrideException($"覆写文件无法按 UTF-8 文本读取：{fileName}（{error.Message}）");
        }
    }

    // ---- 解析（源：Parsing 扩展） ----

    /// <summary>规范化换行、去 BOM、按行拆分（源：lines(of:)）。</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        string normalized = text.Replace("\r\n", "\n");
        if (normalized.StartsWith('\uFEFF'))
        {
            normalized = normalized[1..];
        }

        return normalized.Split('\n');
    }

    /// <summary>规则列表解析：去注释/容错行首 -/去引号（源：parseRuleList）。</summary>
    public static IReadOnlyList<string> ParseRuleList(string text)
    {
        var rules = new List<string>();
        foreach (string line in SplitLines(text))
        {
            string rule = line.Trim();
            if (rule.Length == 0 || rule.StartsWith('#'))
            {
                continue;
            }

            if (rule.StartsWith("- "))
            {
                rule = rule[2..].Trim();
            }

            rule = Unquote(rule);
            if (rule.Length > 0)
            {
                rules.Add(rule);
            }
        }

        return rules;
    }

    /// <summary>规则集子树解析：允许带头、按最小缩进 dedent、提取顶层 provider 名（源：parseRuleProviders）。</summary>
    public static (IReadOnlyList<string> Lines, IReadOnlyList<string> Names) ParseRuleProviders(string text)
    {
        List<string> lines = SplitLines(text).ToList();

        // 允许用户连同 `rule-providers:` 头一起粘贴进来
        int headerIndex = lines.FindIndex(line => !IsBlankOrComment(line));
        if (headerIndex >= 0 && IsTopLevelKeyLine(lines[headerIndex], "rule-providers"))
        {
            lines.RemoveAt(headerIndex);
        }

        List<string> contentLines = lines.Where(line => !IsBlankOrComment(line)).ToList();
        if (contentLines.Count == 0)
        {
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        int baseIndent = contentLines.Min(IndentWidth);
        var names = new List<string>();
        for (int offset = 0; offset < lines.Count; offset++)
        {
            string line = lines[offset];
            if (IsBlankOrComment(line) || IndentWidth(line) != baseIndent)
            {
                continue;
            }

            string? name = MappingKey(line);
            if (name is null)
            {
                throw RuleOverrideException.InvalidRuleProviders(offset + 1);
            }

            names.Add(name);
        }

        List<string> dedented = lines
            .Select(line => IsBlankOrComment(line)
                ? line.Trim()
                : line[Math.Min(baseIndent, line.Length)..])
            .ToList();
        return (TrimBlankEdges(dedented), names);
    }

    // ---- 拼接（源：Splicing 扩展） ----

    /// <summary>把覆写内容拼进配置文本（源：apply——providers 先拼、rules 后拼，输出以换行结尾）。</summary>
    public static string Apply(RuleOverrideContent content, string configText)
    {
        List<string> lines = SplitLines(configText).ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (content.RuleProviderLines.Count > 0)
        {
            SpliceRuleProviders(content, lines);
        }

        if (content.PrependRules.Count > 0 || content.AppendRules.Count > 0)
        {
            SpliceRules(content, lines);
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>rules 块拼接：无块追加整块；append 插 MATCH/FINAL 前（无则末尾）、prepend 插块首（源：spliceRules）。</summary>
    private static void SpliceRules(RuleOverrideContent content, List<string> lines)
    {
        int? header = BlockHeaderIndex(lines, "rules", "[]");
        if (header is null)
        {
            lines.Add("rules:");
            foreach (string rule in content.PrependRules.Concat(content.AppendRules))
            {
                lines.Add($"  - {Quote(rule)}");
            }

            return;
        }

        int end = BlockEnd(lines, header.Value);
        var itemIndices = new List<int>();
        for (int index = header.Value + 1; index < end; index++)
        {
            if (lines[index].TrimStart().StartsWith('-'))
            {
                itemIndices.Add(index);
            }
        }

        string indent = itemIndices.Count > 0
            ? new string(' ', lines[itemIndices[0]].TakeWhile(ch => ch == ' ').Count())
            : "  ";

        // 追加规则先插：插入点在前置规则之后，先插不会让前置的下标失效
        if (content.AppendRules.Count > 0)
        {
            int? matchIndex = null;
            for (int i = itemIndices.Count - 1; i >= 0; i--)
            {
                if (IsFinalRuleLine(lines[itemIndices[i]]))
                {
                    matchIndex = itemIndices[i];
                    break;
                }
            }

            int insertAt = matchIndex
                ?? (itemIndices.Count > 0 ? itemIndices[^1] + 1 : header.Value + 1);
            lines.InsertRange(insertAt, content.AppendRules.Select(rule => $"{indent}- {Quote(rule)}").ToList());
        }

        if (content.PrependRules.Count > 0)
        {
            lines.InsertRange(header.Value + 1, content.PrependRules.Select(rule => $"{indent}- {Quote(rule)}").ToList());
        }
    }

    /// <summary>rule-providers 块拼接：无块追加；同名条目整条删除后在块尾插入（源：spliceRuleProviders）。</summary>
    private static void SpliceRuleProviders(RuleOverrideContent content, List<string> lines)
    {
        int? header = BlockHeaderIndex(lines, "rule-providers", "{}");
        if (header is null)
        {
            lines.Add("rule-providers:");
            foreach (string line in IndentLines(content.RuleProviderLines, "  "))
            {
                lines.Add(line);
            }

            return;
        }

        int end = BlockEnd(lines, header.Value);
        string? childIndent = null;
        for (int index = header.Value + 1; index < end; index++)
        {
            if (!IsBlankOrComment(lines[index]))
            {
                childIndent = new string(' ', lines[index].TakeWhile(ch => ch == ' ').Count());
                break;
            }
        }

        childIndent ??= "  ";

        // 与覆写同名的订阅条目整条删掉，以覆写为准
        var overridden = content.RuleProviderNames.ToHashSet(StringComparer.Ordinal);
        int i = header.Value + 1;
        while (i < end)
        {
            string line = lines[i];
            if (IsBlankOrComment(line) || IndentWidth(line) != childIndent.Length
                || MappingKey(line) is not { } name || !overridden.Contains(name))
            {
                i++;
                continue;
            }

            int entryEnd = i + 1;
            while (entryEnd < end
                   && (IsBlankOrComment(lines[entryEnd]) || IndentWidth(lines[entryEnd]) > childIndent.Length))
            {
                entryEnd++;
            }

            int removed = entryEnd - i;
            lines.RemoveRange(i, removed);
            end -= removed;
        }

        int insertAt = end;
        while (insertAt > header.Value + 1 && IsBlankOrComment(lines[insertAt - 1]))
        {
            insertAt--;
        }

        lines.InsertRange(insertAt, IndentLines(content.RuleProviderLines, childIndent).ToList());
    }

    /// <summary>找顶层 `key:`；空行内写法（key: [] / {}）改写成块式头，非空行内报错（源：blockHeaderIndex）。</summary>
    private static int? BlockHeaderIndex(List<string> lines, string key, string emptyFlow)
    {
        int index = lines.FindIndex(line => IsTopLevelKeyLine(line, key));
        if (index < 0)
        {
            return null;
        }

        string value = InlineValue(lines[index], key);
        if (value.Length == 0)
        {
            return index;
        }

        if (value.Replace(" ", string.Empty) == emptyFlow)
        {
            lines[index] = $"{key}:";
            return index;
        }

        throw RuleOverrideException.UnsupportedFlowStyle(key);
    }

    /// <summary>顶层块在下一个顶层键（第 0 列、非列表项、非注释）或文档分隔符处结束（源：blockEnd）。</summary>
    private static int BlockEnd(List<string> lines, int header)
    {
        int index = header + 1;
        while (index < lines.Count)
        {
            string line = lines[index];
            if (IndentWidth(line) == 0 && !IsBlankOrComment(line))
            {
                if (line.StartsWith("---") || line.StartsWith("..."))
                {
                    break;
                }

                if (!line.StartsWith('-'))
                {
                    break;
                }
            }

            index++;
        }

        return index;
    }

    /// <summary>MATCH / FINAL 行判定（源：isFinalRuleLine）。</summary>
    private static bool IsFinalRuleLine(string line)
    {
        string rule = line.Trim();
        if (!rule.StartsWith('-'))
        {
            return false;
        }

        rule = Unquote(rule[1..].Trim()).ToUpperInvariant();
        return rule.StartsWith("MATCH,") || rule.StartsWith("FINAL,") || rule is "MATCH" or "FINAL";
    }

    // ---- 行辅助（源：Line helpers 扩展） ----

    internal static bool IsTopLevelKeyLine(string line, string key)
    {
        if (IndentWidth(line) != 0)
        {
            return false;
        }

        return line.StartsWith($"{key}:")
            && (line.Length == key.Length + 1 || line[key.Length + 1] == ' ');
    }

    private static string InlineValue(string line, string key)
    {
        string value = line[(key.Length + 1)..];
        int commentIndex = value.IndexOf(" #", StringComparison.Ordinal);
        if (commentIndex >= 0)
        {
            value = value[..commentIndex];
        }

        return value.Trim();
    }

    internal static bool IsBlankOrComment(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith('#');
    }

    internal static int IndentWidth(string line) =>
        line.TakeWhile(ch => ch is ' ' or '\t').Count();

    /// <summary>`name:` / `"name":` 的键名；非映射项返回 null（源：mappingKey）。</summary>
    internal static string? MappingKey(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith('-'))
        {
            return null;
        }

        if (trimmed.Length > 0 && (trimmed[0] == '"' || trimmed[0] == '\''))
        {
            char quote = trimmed[0];
            int close = trimmed.IndexOf(quote, 1);
            if (close > 0 && trimmed.Length > close + 1 && trimmed[close + 1] == ':')
            {
                return trimmed[1..close];
            }

            return null;
        }

        int colon = trimmed.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        string after = trimmed[(colon + 1)..];
        if (after.Length > 0 && !after.StartsWith(' '))
        {
            return null;
        }

        string name = trimmed[..colon].Trim();
        return name.Length > 0 ? name : null;
    }

    internal static string Unquote(string value)
    {
        if (value.Length >= 2
            && (value[0] == '"' || value[0] == '\'')
            && value[^1] == value[0])
        {
            return value[1..^1];
        }

        return value;
    }

    /// <summary>统一输出双引号标量（源：quoted——转义反斜杠与双引号）。</summary>
    internal static string Quote(string rule) =>
        "\"" + rule.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static IReadOnlyList<string> IndentLines(IReadOnlyList<string> lines, string indent) =>
        lines.Select(line => line.Length == 0 ? line : indent + line).ToList();

    private static IReadOnlyList<string> TrimBlankEdges(List<string> lines)
    {
        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
