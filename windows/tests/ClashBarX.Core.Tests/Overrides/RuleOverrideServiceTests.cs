using System.Text;
using ClashBarX.Core.Overrides;

namespace ClashBarX.Core.Tests.Overrides;

/// <summary>规则覆写解析/拼接规格测试（源：RuleOverrideService.swift 逐行对照）。</summary>
public class RuleOverrideServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"clashbarx-override-{Guid.NewGuid():N}");

    private RuleOverrideService CreateService() =>
        new(Path.Combine(_root, "overrides"), Path.Combine(_root, "state", "runtime"));

    private string TempFile(string content)
    {
        string path = Path.Combine(_root, $"config-{Guid.NewGuid():N}.yaml");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- ParseRuleList ----

    [Fact]
    public void 规则列表解析_去注释空行容错前缀与引号()
    {
        string text = "\uFEFF\r\n# 注释\n  DOMAIN-SUFFIX,example.com,DIRECT  \n- GEOIP,CN,Demo\n\"MATCH,Pay\"\n\n";
        IReadOnlyList<string> rules = RuleOverrideService.ParseRuleList(text);

        Assert.Equal(new[] { "DOMAIN-SUFFIX,example.com,DIRECT", "GEOIP,CN,Demo", "MATCH,Pay" }, rules);
    }

    // ---- ParseRuleProviders ----

    [Fact]
    public void 规则集解析_带头去除并dedent提取名称()
    {
        string text =
            """
            rule-providers:
              my-rules:
                type: http
              other:
                type: file
            """;
        (IReadOnlyList<string> lines, IReadOnlyList<string> names) =
            RuleOverrideService.ParseRuleProviders(text);

        Assert.Equal(new[] { "my-rules", "other" }, names);
        // dedent 后子树不含头、顶层键在第 0 列
        Assert.Equal("my-rules:", lines[0]);
        Assert.Equal("  type: http", lines[1]);
    }

    [Fact]
    public void 规则集解析_引号键与统一缩进dedent()
    {
        // 整块统一缩进（粘贴片段的典型形态）：baseIndent = 全体内容行最小缩进
        string text = "  \"quoted-name\":\n    type: http\n    url: https://x\n";
        (IReadOnlyList<string> lines, IReadOnlyList<string> names) =
            RuleOverrideService.ParseRuleProviders(text);

        Assert.Equal(new[] { "quoted-name" }, names);
        Assert.Equal("\"quoted-name\":", lines[0]);   // 剥 2 后顶格
        Assert.Equal("  type: http", lines[1]);   // 子行相对缩进保留
    }

    [Fact]
    public void 规则集解析_非映射项报错带行号()
    {
        string text = "- 不是映射项\n";
        RuleOverrideException error = Assert.Throws<RuleOverrideException>(
            () => RuleOverrideService.ParseRuleProviders(text));
        Assert.Contains("第 1 行", error.Message);
    }

    // ---- Apply：rules 拼接 ----

    [Fact]
    public void 拼接_prepend插块首_append插MATCH前并沿用缩进()
    {
        string config =
            """
            mode: rule
            rules:
              - GEOIP,CN,Demo
              - MATCH,Pay
            proxies: []
            """;
        var content = new RuleOverrideContent(
            new[] { "DOMAIN-SUFFIX,pre.example,DIRECT" },
            new[] { "GEOIP,lan,DIRECT" },
            Array.Empty<string>(),
            Array.Empty<string>());

        string merged = RuleOverrideService.Apply(content, config);

        string[] lines = merged.Split('\n');
        int header = Array.FindIndex(lines, line => line == "rules:");
        Assert.Equal("  - \"DOMAIN-SUFFIX,pre.example,DIRECT\"", lines[header + 1]);   // prepend 紧跟块头（新行统一双引号）
        Assert.Equal("  - GEOIP,CN,Demo", lines[header + 2]);   // 原有规则行原样保留（源：只转插入行）
        int appendIndex = Array.FindIndex(lines, line => line.Contains("GEOIP,lan"));
        int matchIndex = Array.FindIndex(lines, line => line.Contains("MATCH,Pay"));
        Assert.True(appendIndex > header && matchIndex > appendIndex);   // append 在 MATCH 之前
        Assert.EndsWith("\n", merged);   // 输出以换行结尾
    }

    [Fact]
    public void 拼接_无rules块时追加整块()
    {
        string config = "mode: rule\n";
        var content = new RuleOverrideContent(
            new[] { "DOMAIN,pre,DIRECT" }, new[] { "GEOIP,app,DIRECT" },
            Array.Empty<string>(), Array.Empty<string>());

        string merged = RuleOverrideService.Apply(content, config);

        Assert.Contains("rules:\n", merged);
        Assert.Contains("  - \"DOMAIN,pre,DIRECT\"", merged);
        Assert.Contains("  - \"GEOIP,app,DIRECT\"", merged);
    }

    [Fact]
    public void 拼接_append无MATCH时插列表末尾()
    {
        string config = "rules:\n  - GEOIP,CN,Demo\n";
        var content = new RuleOverrideContent(
            Array.Empty<string>(), new[] { "DOMAIN-SUFFIX,app.example,DIRECT" },
            Array.Empty<string>(), Array.Empty<string>());

        string merged = RuleOverrideService.Apply(content, config);

        string[] lines = merged.Split('\n').Where(line => line.Length > 0).ToArray();
        Assert.Equal("  - \"DOMAIN-SUFFIX,app.example,DIRECT\"", lines[^1]);
    }

    [Fact]
    public void 拼接_规则含特殊字符时双引号转义()
    {
        string config = "rules: []\n";
        var content = new RuleOverrideContent(
            new[] { "DOMAIN,例\\如\"引号\",DIRECT" }, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>());

        string merged = RuleOverrideService.Apply(content, config);

        Assert.Contains("- \"DOMAIN,例\\\\如\\\"引号\\\",DIRECT\"", merged);
    }

    [Fact]
    public void 拼接_空行内写法改写为块式头()
    {
        string config = "rules: []\n";
        var content = new RuleOverrideContent(
            new[] { "DOMAIN,a.com,DIRECT" }, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>());

        string merged = RuleOverrideService.Apply(content, config);

        Assert.Contains("rules:\n  - \"DOMAIN,a.com,DIRECT\"", merged);
    }

    [Fact]
    public void 拼接_非空行内写法报错()
    {
        string config = "rules: [\"MATCH,Pay\"]\n";
        var content = new RuleOverrideContent(
            new[] { "DOMAIN,a.com,DIRECT" }, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>());

        Assert.Throws<RuleOverrideException>(() => RuleOverrideService.Apply(content, config));
    }

    // ---- Apply：rule-providers 拼接 ----

    [Fact]
    public void 拼接_无provider块时追加()
    {
        string config = "mode: rule\n";
        var content = new RuleOverrideContent(
            Array.Empty<string>(), Array.Empty<string>(),
            new[] { "my-rules:", "  type: http" },
            new[] { "my-rules" });

        string merged = RuleOverrideService.Apply(content, config);

        Assert.Contains("rule-providers:\n  my-rules:\n    type: http", merged);
    }

    [Fact]
    public void 拼接_同名provider整条删除以覆写为准()
    {
        string config =
            """
            rule-providers:
              old-rules:
                type: file
              my-rules:
                type: http
                url: https://sub.example/rules.yaml
            rules: []
            """;
        var content = new RuleOverrideContent(
            Array.Empty<string>(), Array.Empty<string>(),
            new[] { "my-rules:", "  type: file", "  path: ./local.yaml" },
            new[] { "my-rules" });

        string merged = RuleOverrideService.Apply(content, config);

        Assert.Contains("my-rules:\n    type: file\n    path: ./local.yaml", merged);   // 覆写版插入（childIndent=2 前缀）
        Assert.DoesNotContain("https://sub.example/rules.yaml", merged);   // 订阅同名条目被删
        Assert.Contains("old-rules:", merged);   // 不同名条目保留
    }

    [Fact]
    public void 拼接_原配置结尾空行被收敛()
    {
        string config = "mode: rule\n\n\n";
        var content = new RuleOverrideContent(
            new[] { "DOMAIN,a.com,DIRECT" }, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>());

        string merged = RuleOverrideService.Apply(content, config);

        Assert.DoesNotContain("\n\n", merged);
    }

    // ---- 文件读写与运行时配置 ----

    [Fact]
    public void 文件不存在时编辑器显示模板不落盘()
    {
        RuleOverrideService service = CreateService();

        string text = service.EditableText(RuleOverrideFile.Prepend);

        Assert.Contains("前置规则", text);
        Assert.False(File.Exists(service.FilePath(RuleOverrideFile.Prepend)));
    }

    [Fact]
    public void 保存后往返读取并解析计数()
    {
        RuleOverrideService service = CreateService();
        service.Write("DOMAIN-SUFFIX,a.com,DIRECT\n# 注释\n", RuleOverrideFile.Prepend);
        service.Write("GEOIP,lan,DIRECT\n", RuleOverrideFile.Append);
        service.Write("my-rules:\n  type: http\n", RuleOverrideFile.RuleProviders);

        RuleOverrideContent content = service.LoadContent();

        Assert.Equal(1, content.Counts.Prepend);
        Assert.Equal(1, content.Counts.Append);
        Assert.Equal(1, content.Counts.RuleProviders);
        Assert.False(content.Counts.IsEmpty);
    }

    [Fact]
    public void 运行时配置生成并沿用原文件名()
    {
        RuleOverrideService service = CreateService();
        string original = TempFile("mode: rule\nrules:\n  - MATCH,Pay\n");
        var content = new RuleOverrideContent(
            new[] { "DOMAIN,a.com,DIRECT" }, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>());

        string runtimePath = service.WriteRuntimeConfig(original, content);

        Assert.Equal(Path.GetFileName(original), Path.GetFileName(runtimePath));
        Assert.Contains("DOMAIN,a.com,DIRECT", File.ReadAllText(runtimePath));
        Assert.DoesNotContain("DOMAIN,a.com,DIRECT", File.ReadAllText(original));   // 原配置不被改写
    }

    [Fact]
    public void 原子写不残留临时文件()
    {
        RuleOverrideService service = CreateService();
        service.Write("DOMAIN,a.com,DIRECT\n", RuleOverrideFile.Prepend);

        Assert.Equal("DOMAIN,a.com,DIRECT\n", File.ReadAllText(service.FilePath(RuleOverrideFile.Prepend)));
        Assert.False(Directory.GetFiles(Path.GetDirectoryName(service.FilePath(RuleOverrideFile.Prepend))!)
            .Any(path => path.EndsWith(".tmp")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
