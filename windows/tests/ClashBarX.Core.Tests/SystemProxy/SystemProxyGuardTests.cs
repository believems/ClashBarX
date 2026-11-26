using ClashBarX.Core.SystemProxy;

namespace ClashBarX.Core.Tests.SystemProxy;

/// <summary>绕过列表归一化规格测试（源：normalizedSystemProxyExceptionValues）。</summary>
public class SystemProxyBypassTests
{
    [Fact]
    public void 归一化去空白去空项并保序()
    {
        IReadOnlyList<string> result = SystemProxyBypass.Normalize(new[] { " a ", "", "b", " a ", null! });
        Assert.Equal(new[] { "a", "b" }, result);
    }

    [Fact]
    public void 大小写不敏感去重保留首次出现()
    {
        IReadOnlyList<string> result = SystemProxyBypass.Normalize(new[] { "LocalHost", "localhost", "10.*", "10.*" });
        Assert.Equal(new[] { "LocalHost", "10.*" }, result);
    }

    [Fact]
    public void 解析ProxyOverride字符串()
    {
        IReadOnlyList<string> result = SystemProxyBypass.Parse("localhost;127.*;;10.*; 172.* ");
        Assert.Equal(new[] { "localhost", "127.*", "10.*", "172.*" }, result);
    }

    [Fact]
    public void 渲染覆盖列表为分号串()
    {
        Assert.Equal("localhost;127.*;10.*", SystemProxyDefaults.RenderOverride(new[] { "localhost", "127.*", "10.*" }));
    }

    [Fact]
    public void 默认绕过列表包含环回与私网段()
    {
        Assert.Contains("localhost", SystemProxyDefaults.DefaultBypassList);
        Assert.Contains("127.*", SystemProxyDefaults.DefaultBypassList);
        Assert.Contains("<local>", SystemProxyDefaults.DefaultBypassList);
        Assert.Contains("10.*", SystemProxyDefaults.DefaultBypassList);
    }
}

/// <summary>守护比较逻辑规格测试（源：systemProxyStillMatchesExpectedTarget）。</summary>
public class SystemProxyGuardTests
{
    private static SystemProxyTarget Target(
        string host = "127.0.0.1", int port = 7890, string[]? bypass = null) =>
        new(host, port, bypass ?? new[] { "localhost", "127.*", "10.*" });

    [Fact]
    public void 全等时匹配()
    {
        var snapshot = new SystemProxySnapshot(true, "127.0.0.1:7890", new[] { "localhost", "127.*", "10.*" });
        Assert.True(SystemProxyGuard.Matches(snapshot, Target()));
        Assert.False(SystemProxyGuard.NeedsRewrite(snapshot, Target()));
    }

    [Fact]
    public void 未启用不匹配()
    {
        var snapshot = new SystemProxySnapshot(false, "127.0.0.1:7890", new[] { "localhost", "127.*", "10.*" });
        Assert.True(SystemProxyGuard.NeedsRewrite(snapshot, Target()));
    }

    [Fact]
    public void 端口不同不匹配()
    {
        var snapshot = new SystemProxySnapshot(true, "127.0.0.1:7891", new[] { "localhost", "127.*", "10.*" });
        Assert.True(SystemProxyGuard.NeedsRewrite(snapshot, Target()));
    }

    [Fact]
    public void 绕过列表差异不匹配()
    {
        var snapshot = new SystemProxySnapshot(true, "127.0.0.1:7890", new[] { "localhost", "127.*" });
        Assert.True(SystemProxyGuard.NeedsRewrite(snapshot, Target()));
    }

    [Fact]
    public void 绕过列表顺序差异不匹配()
    {
        var snapshot = new SystemProxySnapshot(true, "127.0.0.1:7890", new[] { "127.*", "localhost", "10.*" });
        Assert.True(SystemProxyGuard.NeedsRewrite(snapshot, Target()));
    }

    [Fact]
    public void 环回等价主机视为匹配()
    {
        // 外部工具写入 localhost:7890，期望 127.0.0.1:7890 → 视为一致（源：loopback 归一化）
        var snapshot = new SystemProxySnapshot(true, "localhost:7890", new[] { "localhost", "127.*", "10.*" });
        Assert.True(SystemProxyGuard.Matches(snapshot, Target()));
    }

    [Fact]
    public void 无意图时不回写()
    {
        var snapshot = new SystemProxySnapshot(false, null, Array.Empty<string>());
        Assert.False(SystemProxyGuard.NeedsRewrite(snapshot, expected: null));
    }
}
