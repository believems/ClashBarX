import Foundation
import Yams

/// 内网前置代理注入（Issue #2）：在给内核的最终配置上做三处注入——
/// ① `proxies` 每个静态节点加 `dialer-proxy`（节点连接经企业代理拨出，域名解析
///    由代理端完成，绕开内网 DNS 黑洞）；
/// ② 每个 `proxy-providers` 项加 `override.dialer-proxy`（provider 拉取的订阅节点
///    统一走前置代理）与 `proxy`（**provider 自身的订阅下载/更新也走企业代理**——
///    内网订阅自愈的关键通道）；
/// ③ 追加企业代理节点定义（type: http）。
///
/// 注入只发生在生成给内核的运行时配置上，订阅源文件保持原样。
enum CorpProxyInjector {
    /// 注入节点名带内部前缀，避免与用户节点重名。
    static let proxyName = "clashbarx-corp-forward-proxy"

    enum InjectorError: Error, LocalizedError {
        case invalidDocument
        case invalidProxies
        case invalidProviders

        var errorDescription: String? {
            switch self {
            case .invalidDocument:
                "Configuration is not a valid YAML mapping"
            case .invalidProxies:
                "proxies section is not a valid list"
            case .invalidProviders:
                "proxy-providers section is not a valid list"
            }
        }
    }

    /// 配置是否具备注入对象（存在 proxies 或 proxy-providers 任一节）。
    static func needsInjection(configYAML: String) -> Bool {
        guard let document = try? Yams.compose(yaml: configYAML), document.mapping != nil else {
            return false
        }
        return document["proxies"]?.sequence != nil || document["proxy-providers"]?.mapping != nil
    }

    /// 注入并返回新配置文本；无 proxies 且无 proxy-providers 时返回 nil（调用方沿用原配置）。
    static func inject(configYAML: String, settings: CorpProxySettings) throws -> String? {
        guard var document = try Yams.compose(yaml: configYAML), document.mapping != nil else {
            throw InjectorError.invalidDocument
        }

        let injectedProvider = try injectProviders(&document, settings: settings)
        let injectedProxies = try injectStaticProxies(&document, settings: settings)
        guard injectedProvider || injectedProxies else { return nil }

        try appendCorpProxyNode(&document, settings: settings)
        return try Yams.serialize(node: document)
    }

    // MARK: - Static proxies

    private static func injectStaticProxies(_ document: inout Node, settings: CorpProxySettings) throws -> Bool {
        guard let proxies = document["proxies"] else { return false }
        guard let sequence = proxies.sequence else { throw InjectorError.invalidProxies }

        var nextSequence: [Node] = []
        for var item in sequence {
            if item.mapping != nil {
                // 产品语义：开关开启即全量接管——已有 dialer-proxy 也覆盖。
                item["dialer-proxy"] = scalar(Self.proxyName)
            }
            nextSequence.append(item)
        }
        document["proxies"] = Node(nextSequence)
        return !sequence.isEmpty
    }

    // MARK: - Proxy providers

    /// proxy-providers 的形态是 mapping（provider 名 → 配置），逐项注入。
    private static func injectProviders(_ document: inout Node, settings: CorpProxySettings) throws -> Bool {
        guard let providers = document["proxy-providers"] else { return false }
        guard let pairs = providers.mapping else { throw InjectorError.invalidProviders }

        var nextPairs: [(Node, Node)] = []
        for (name, var item) in pairs {
            if item.mapping != nil {
                // provider 自身的订阅拉取走企业代理（内网订阅自愈通道）。
                item["proxy"] = scalar(Self.proxyName)
                // provider 拉取的节点统一注入 dialer-proxy（mihomo override 官方支持）。
                var override = item["override"] ?? Node([(Node, Node)]())
                if override.mapping != nil {
                    override["dialer-proxy"] = scalar(Self.proxyName)
                    item["override"] = override
                }
            }
            nextPairs.append((name, item))
        }
        document["proxy-providers"] = Node(nextPairs)
        return !pairs.isEmpty
    }

    // MARK: - Corp proxy node

    private static func appendCorpProxyNode(_ document: inout Node, settings: CorpProxySettings) throws {
        // Node.sequence 是 Node.Sequence 包装结构，构造 Node 需要 [Node]。
        var sequence: [Node] = Array(document["proxies"]?.sequence ?? [])
        if document["proxies"]?.mapping != nil { throw InjectorError.invalidProxies }

        var corpNode = Node([(Node, Node)]())
        corpNode["name"] = scalar(Self.proxyName)
        corpNode["type"] = scalar("http")
        corpNode["server"] = scalar(settings.trimmedHost)
        corpNode["port"] = Node("\(settings.port)", Tag(.int))
        if settings.hasCredentials {
            corpNode["username"] = scalar(settings.username)
            corpNode["password"] = scalar(settings.password)
        }
        sequence.append(corpNode)
        document["proxies"] = Node(sequence)
    }

    private static func scalar(_ value: String) -> Node {
        Node(value, Tag(.str))
    }
}
