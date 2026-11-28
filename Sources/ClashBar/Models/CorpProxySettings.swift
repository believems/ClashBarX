import Foundation

/// 内网前置代理设置（Issue #2）：集团内网仅允许经企业 HTTP 代理出外网时，
/// 节点连接与订阅拉取统一经该代理拨出（mihomo dialer-proxy 语义）。
struct CorpProxySettings: Equatable {
    var isEnabled: Bool
    var host: String
    var port: Int
    var username: String
    var password: String

    var trimmedHost: String {
        self.host.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    var hasCredentials: Bool {
        !self.username.isEmpty || !self.password.isEmpty
    }

    /// 地址与端口就绪（不含 isEnabled 判定）。
    var isValid: Bool {
        !self.trimmedHost.isEmpty && (1...65535).contains(self.port)
    }

    /// 开关打开且配置可用。
    var isReady: Bool {
        self.isEnabled && self.isValid
    }
}

/// App 层请求经企业代理的端点描述（Sendable：跨并发域只传值语义）。
struct CorpProxyEndpoint: Sendable, Equatable {
    let host: String
    let port: Int

    /// URLSession 的代理字典（只在发起请求的并发域内构造，避免 Any 跨域）。
    var urlSessionProxyDictionary: [AnyHashable: Any] {
        [
            kCFNetworkProxiesHTTPEnable: true,
            kCFNetworkProxiesHTTPProxy: self.host,
            kCFNetworkProxiesHTTPPort: self.port,
        ]
    }
}
