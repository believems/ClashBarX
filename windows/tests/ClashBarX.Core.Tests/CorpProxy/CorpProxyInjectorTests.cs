using ClashBarX.Core.CorpProxy;
using YamlDotNet.RepresentationModel;

namespace ClashBarX.Core.Tests.CorpProxy;

/// <summary>
/// 内网前置代理注入规格（Issue #2，对齐 macOS CorpProxyInjector 8 用例口径）：
/// 三处注入、无注入对象返回 nil、非法节抛错、流式+锚点配置可注入且锚点/合并键保留。
/// </summary>
public class CorpProxyInjectorTests
{
    private static readonly CorpProxySettings Ready = new(true, "proxy.corp.local", 8080, "", "");

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string Scalar(YamlMappingNode map, string key) =>
        ((YamlScalarNode)map.Children[new YamlScalarNode(key)]).Value!;

    [Fact]
    public void 静态节点加dialer且provider加proxy与override并追加企业节点()
    {
        const string yaml = """
            proxies:
              - { name: hk-01, type: ss, server: 1.2.3.4, port: 8388 }
            proxy-providers:
              jf:
                type: http
                url: 'https://example/sub'
            """;
        var settings = new CorpProxySettings(true, "proxy.corp.local", 8080, "u1", "p1");

        string injected = CorpProxyInjector.Inject(yaml, settings)!;
        YamlMappingNode root = Parse(injected);
        var proxies = (YamlSequenceNode)root.Children[new YamlScalarNode("proxies")];

        Assert.Equal(2, proxies.Children.Count);
        Assert.Equal(CorpProxyInjector.ProxyName, Scalar((YamlMappingNode)proxies.Children[0], "dialer-proxy"));

        YamlMappingNode corp = (YamlMappingNode)proxies.Children[1];
        Assert.Equal(CorpProxyInjector.ProxyName, Scalar(corp, "name"));
        Assert.Equal("http", Scalar(corp, "type"));
        Assert.Equal("proxy.corp.local", Scalar(corp, "server"));
        Assert.Equal("8080", Scalar(corp, "port"));
        Assert.Equal("u1", Scalar(corp, "username"));
        Assert.Equal("p1", Scalar(corp, "password"));

        var providers = (YamlMappingNode)root.Children[new YamlScalarNode("proxy-providers")];
        YamlMappingNode jf = (YamlMappingNode)providers.Children[new YamlScalarNode("jf")];
        Assert.Equal(CorpProxyInjector.ProxyName, Scalar(jf, "proxy"));
        var overrideMap = (YamlMappingNode)jf.Children[new YamlScalarNode("override")];
        Assert.Equal(CorpProxyInjector.ProxyName, Scalar(overrideMap, "dialer-proxy"));
    }

    [Fact]
    public void 无凭据时企业节点不带username与password()
    {
        const string yaml = "proxies:\n  - { name: a, type: ss, server: s, port: 1 }\n";

        string injected = CorpProxyInjector.Inject(yaml, Ready)!;

        Assert.DoesNotContain("username", injected);
        Assert.DoesNotContain("password", injected);
    }

    [Fact]
    public void 已有dialer被全量接管覆盖()
    {
        const string yaml = "proxies:\n  - { name: a, type: ss, server: s, port: 1, dialer-proxy: other }\n";

        string injected = CorpProxyInjector.Inject(yaml, Ready)!;
        var proxies = (YamlSequenceNode)Parse(injected).Children[new YamlScalarNode("proxies")];

        Assert.Equal(CorpProxyInjector.ProxyName, Scalar((YamlMappingNode)proxies.Children[0], "dialer-proxy"));
    }

    [Fact]
    public void 流式与锚点合并键配置可注入且保留()
    {
        // BOSS 生产配置形态：顶层锚点 + provider 流式映射 + 合并键
        const string yaml = """
            providers: &providers { type: http, interval: 86400 }
            domain: &domain { <<: *providers, behavior: domain, format: mrs }
            proxies: [{ name: IPV4优先, type: direct, udp: true }]
            proxy-providers:
              ads: { <<: *domain, url: 'https://example/ads.mrs', path: ./rules/ads.mrs }
            """;

        string injected = CorpProxyInjector.Inject(yaml, Ready)!;
        YamlMappingNode root = Parse(injected);

        var providers = (YamlMappingNode)root.Children[new YamlScalarNode("proxy-providers")];
        YamlMappingNode ads = (YamlMappingNode)providers.Children[new YamlScalarNode("ads")];
        Assert.Equal(CorpProxyInjector.ProxyName, Scalar(ads, "proxy"));
        // 合并键与锚点别名在生成配置中保留（mihomo 加载需原语义）
        Assert.True(ads.Children.ContainsKey(new YamlScalarNode("<<")));
        Assert.Contains("*providers", injected);
    }

    [Fact]
    public void 无proxies与providers时返回null且不需要注入()
    {
        const string yaml = "mode: rule\nmixed-port: 7890\n";

        Assert.Null(CorpProxyInjector.Inject(yaml, Ready));
        Assert.False(CorpProxyInjector.NeedsInjection(yaml));
    }

    [Fact]
    public void 仅providers也注入并新建proxies节()
    {
        const string yaml = "proxy-providers:\n  jf: { type: http, url: 'https://example/sub' }\n";

        Assert.True(CorpProxyInjector.NeedsInjection(yaml));
        string injected = CorpProxyInjector.Inject(yaml, Ready)!;
        var proxies = (YamlSequenceNode)Parse(injected).Children[new YamlScalarNode("proxies")];

        Assert.Single(proxies.Children);
        Assert.Equal(CorpProxyInjector.ProxyName, Scalar((YamlMappingNode)proxies.Children[0], "name"));
    }

    [Fact]
    public void proxies节非列表抛错()
    {
        const string yaml = "proxies: oops\n";

        Assert.Throws<CorpProxyInjector.InjectorException>(() => CorpProxyInjector.Inject(yaml, Ready));
    }

    [Fact]
    public void 非法YAML按不需要注入降级NeedsInjection为false()
    {
        Assert.False(CorpProxyInjector.NeedsInjection("proxies: [unclosed"));
        Assert.False(CorpProxyInjector.NeedsInjection(""));
    }

    [Theory]
    [InlineData(false, "h", 8080, false)]     // 关开关不 ready
    [InlineData(true, "", 8080, false)]       // 空 host
    [InlineData(true, " h ", 0, false)]       // 端口越界
    [InlineData(true, " h ", 65536, false)]
    [InlineData(true, "h", 1, true)]
    [InlineData(true, "h", 65535, true)]
    public void IsReady组合判定(bool enabled, string host, int port, bool expected)
    {
        var settings = new CorpProxySettings(enabled, host, port, "", "");

        Assert.Equal(expected, settings.IsReady);
    }

    [Fact]
    public void Host去空白与凭据判定()
    {
        var settings = new CorpProxySettings(true, "  proxy.corp  ", 8080, "", "pw");

        Assert.Equal("proxy.corp", settings.TrimmedHost);
        Assert.True(settings.HasCredentials);
    }
}
