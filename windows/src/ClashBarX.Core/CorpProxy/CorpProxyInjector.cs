using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ClashBarX.Core.CorpProxy;

/// <summary>
/// 内网前置代理注入（Issue #2，源：macOS CorpProxyInjector）：在给内核的最终配置上做三处注入——
/// ① <c>proxies</c> 每个静态节点加 <c>dialer-proxy</c>（节点连接经企业代理拨出，域名解析由代理端
/// 完成，绕开内网 DNS 黑洞；开关开启即全量接管，已有 dialer-proxy 也覆盖）；
/// ② 每个 <c>proxy-providers</c> 项加 <c>override.dialer-proxy</c>（provider 拉取的订阅节点统一走
/// 前置代理）与 <c>proxy</c>（provider 自身的订阅下载/更新也走企业代理——内网订阅自愈的关键通道）；
/// ③ 追加企业代理节点定义（type: http，名字带内部前缀避免与用户节点重名）。
/// 注入只发生在生成给内核的运行时配置上，订阅源文件保持原样。
/// YAML 对象模型用 YamlDotNet 通用图（Mac 用 Yams）：锚点/别名/合并键原样保留，
/// 流式写法归一为块式、注释丢弃——与 Mac 生成运行时配置的副作用同口径。
/// </summary>
public static class CorpProxyInjector
{
    /// <summary>注入节点名带内部前缀，避免与用户节点重名（源：proxyName）。</summary>
    public const string ProxyName = "clashbarx-corp-forward-proxy";

    private const string ProxiesKey = "proxies";
    private const string ProvidersKey = "proxy-providers";

    /// <summary>注入失败原因（源：InjectorError 三态）。</summary>
    public sealed class InjectorException : Exception
    {
        public InjectorException(string message) : base(message)
        {
        }
    }

    /// <summary>配置是否具备注入对象（存在 proxies 或 proxy-providers 任一节）。</summary>
    public static bool NeedsInjection(string configYaml) =>
        TryLoadRoot(configYaml) is { } root && HasInjectableSection(root);

    /// <summary>
    /// 注入并返回新配置文本；无 proxies 且无 proxy-providers 时返回 null（调用方沿用原配置）。
    /// </summary>
    public static string? Inject(string configYaml, CorpProxySettings settings)
    {
        YamlMappingNode root = TryLoadRoot(configYaml)
            ?? throw new InjectorException("Configuration is not a valid YAML mapping");

        bool injectedProviders = InjectProviders(root);
        bool injectedProxies = InjectStaticProxies(root);
        if (!injectedProviders && !injectedProxies)
        {
            return null;
        }

        AppendCorpProxyNode(root, settings);
        return Serialize(root);
    }

    private static YamlMappingNode? TryLoadRoot(string configYaml)
    {
        if (string.IsNullOrWhiteSpace(configYaml))
        {
            return null;
        }

        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(configYaml);
            stream.Load(reader);
            return stream.Documents.Count == 1 ? stream.Documents[0].RootNode as YamlMappingNode : null;
        }
        catch (Exception error) when (error is YamlException or IOException)
        {
            return null;
        }
    }

    private static bool HasInjectableSection(YamlMappingNode root) =>
        root.Children.TryGetValue(Scalar(ProxiesKey), out YamlNode? proxies) && proxies is YamlSequenceNode
        || root.Children.TryGetValue(Scalar(ProvidersKey), out YamlNode? providers2) && providers2 is YamlMappingNode;

    /// <summary>静态节点全量加 dialer-proxy（产品语义：开关开启即接管，已有值覆盖）。</summary>
    private static bool InjectStaticProxies(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(Scalar(ProxiesKey), out YamlNode? node))
        {
            return false;
        }

        if (node is not YamlSequenceNode sequence)
        {
            throw new InjectorException("proxies section is not a valid list");
        }

        foreach (YamlNode item in sequence.Children)
        {
            if (item is YamlMappingNode mapping)
            {
                mapping.Children[Scalar("dialer-proxy")] = Scalar(ProxyName);
            }
        }

        return sequence.Children.Count > 0;
    }

    /// <summary>proxy-providers 为 mapping（provider 名 → 配置），逐项注入 proxy 与 override.dialer-proxy。</summary>
    private static bool InjectProviders(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(Scalar(ProvidersKey), out YamlNode? node))
        {
            return false;
        }

        if (node is not YamlMappingNode providers)
        {
            throw new InjectorException("proxy-providers section is not a valid list");
        }

        foreach (KeyValuePair<YamlNode, YamlNode> pair in providers.Children)
        {
            if (pair.Value is not YamlMappingNode item)
            {
                continue;
            }

            // provider 自身的订阅拉取走企业代理（内网订阅自愈通道）
            item.Children[Scalar("proxy")] = Scalar(ProxyName);

            // provider 拉取的节点统一注入 dialer-proxy（mihomo override 官方支持）
            if (!item.Children.TryGetValue(Scalar("override"), out YamlNode? overrideNode)
                || overrideNode is not YamlMappingNode overrideMapping)
            {
                overrideMapping = new YamlMappingNode();
                item.Children[Scalar("override")] = overrideMapping;
            }

            overrideMapping.Children[Scalar("dialer-proxy")] = Scalar(ProxyName);
        }

        return providers.Children.Count > 0;
    }

    /// <summary>追加企业代理节点定义；配置无 proxies 节时新建（Mac 同口径）。</summary>
    private static void AppendCorpProxyNode(YamlMappingNode root, CorpProxySettings settings)
    {
        if (root.Children.TryGetValue(Scalar(ProxiesKey), out YamlNode? node) && node is YamlMappingNode)
        {
            throw new InjectorException("proxies section is not a valid list");
        }

        var sequence = node as YamlSequenceNode ?? new YamlSequenceNode();
        var corpNode = new YamlMappingNode
        {
            { Scalar("name"), Scalar(ProxyName) },
            { Scalar("type"), Scalar("http") },
            { Scalar("server"), Scalar(settings.TrimmedHost) },
            { Scalar("port"), Scalar(settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
        };
        if (settings.HasCredentials)
        {
            corpNode.Add(Scalar("username"), QuotedScalar(settings.Username));
            corpNode.Add(Scalar("password"), QuotedScalar(settings.Password));
        }

        sequence.Add(corpNode);
        root.Children[Scalar(ProxiesKey)] = sequence;
    }

    private static string Serialize(YamlMappingNode root)
    {
        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        // assignAnchors 必须 false：true 会给每个节点编造数字锚（甚至插进 flow 映射内部），
        // 产出不可回读的 YAML（探针实测）；false 保留源文件原有锚点/别名/合并键且回读干净。
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static YamlScalarNode Scalar(string value) => new(value);

    /// <summary>
    /// 用户手输的字符串字段用双引号标量（源：Yams Node+Tag(.str) 输出时对歧义值加引号的语义）：
    /// 纯数字密码/用户名按 plain 输出会被 YAML 解析成数值，mihomo 解 http 节点 string 字段直接报错。
    /// </summary>
    private static YamlScalarNode QuotedScalar(string value) => new(value) { Style = ScalarStyle.DoubleQuoted };
}
