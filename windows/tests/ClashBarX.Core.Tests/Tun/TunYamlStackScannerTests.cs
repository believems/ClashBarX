using ClashBarX.Core.Tun;

namespace ClashBarX.Core.Tests.Tun;

/// <summary>YAML tun.stack 声明扫描规格测试（源：selectedConfigDeclaresTunSwift 逐行对照）。</summary>
public class TunYamlStackScannerTests
{
    [Fact]
    public void DeclaresTunStack_TrueWhenNestedUnderTopLevelTun()
    {
        const string yaml = """
            port: 7890
            tun:
              enable: true
              stack: system
            rules:
              - MATCH,DIRECT
            """;
        Assert.True(TunYamlStackScanner.DeclaresTunStack(yaml));
    }

    [Fact]
    public void DeclaresTunStack_TrueWithInlineValue()
    {
        const string yaml = "tun:\n  stack: gvisor\n";
        Assert.True(TunYamlStackScanner.DeclaresTunStack(yaml));
    }

    [Fact]
    public void DeclaresTunStack_FalseWhenTunAbsent()
    {
        Assert.False(TunYamlStackScanner.DeclaresTunStack("port: 7890\nrules:\n  - MATCH,DIRECT\n"));
    }

    [Fact]
    public void DeclaresTunStack_FalseWhenTunBlockEmpty()
    {
        Assert.False(TunYamlStackScanner.DeclaresTunStack("tun:\n  enable: true\n"));
    }

    [Fact]
    public void DeclaresTunStack_FalseWhenStackIsTopLevelKey()
    {
        // 顶层 stack: 与 tun: 块无关（源：childLineExists 只扫块内缩进行）
        Assert.False(TunYamlStackScanner.DeclaresTunStack("tun:\n  enable: true\nstack: system\n"));
    }

    [Fact]
    public void DeclaresTunStack_FalseWhenStackInOtherBlock()
    {
        const string yaml = """
            tun:
              enable: true
            dns:
              stack: not-this-one
            """;
        Assert.False(TunYamlStackScanner.DeclaresTunStack(yaml));
    }

    [Fact]
    public void DeclaresTunStack_IgnoresCommentedChild()
    {
        Assert.False(TunYamlStackScanner.DeclaresTunStack("tun:\n  # stack: system\n  enable: true\n"));
    }

    [Fact]
    public void DeclaresTunStack_NormalizesCrlf()
    {
        Assert.True(TunYamlStackScanner.DeclaresTunStack("tun:\r\n  stack: mixed\r\n"));
    }

    [Fact]
    public void DeclaresTunStack_TrueWhenLaterTopLevelMappingClosesBlock()
    {
        const string yaml = """
            tun:
              enable: true
              stack: mips
            proxy-groups:
              - name: demo
            """;
        Assert.True(TunYamlStackScanner.DeclaresTunStack(yaml));
    }
}
