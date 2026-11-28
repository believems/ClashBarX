import Foundation
import XCTest
import Yams
@testable import ClashBar

/// 内网前置代理注入（Issue #2）：三种注入形态与边界。
final class CorpProxyInjectorTests: XCTestCase {
    private let settings = CorpProxySettings(
        isEnabled: true,
        host: "10.1.2.3",
        port: 8080,
        username: "",
        password: "")

    private let credentialSettings = CorpProxySettings(
        isEnabled: true,
        host: "proxy.corp.example",
        port: 3128,
        username: "user",
        password: "pass")

    func testStaticProxiesGetDialerProxy() throws {
        let yaml = """
        proxies:
          - name: "node-a"
            type: vmess
            server: a.example.com
            port: 443
          - name: "node-b"
            type: ss
            server: b.example.com
            port: 8383
        rules:
          - MATCH,DIRECT
        """
        let injected = try XCTUnwrap(CorpProxyInjector.inject(configYAML: yaml, settings: self.settings))
        let document = try XCTUnwrap(Yams.compose(yaml: injected))
        let proxies = try XCTUnwrap(document["proxies"]?.sequence)
        XCTAssertEqual(proxies.count, 3)
        // 前两个是原订阅节点（均已注入），最后一个 corp 节点自身不自指。
        for item in proxies.dropLast() {
            XCTAssertEqual(item["dialer-proxy"]?.scalar?.string, CorpProxyInjector.proxyName)
        }
        XCTAssertNil(proxies.last?["dialer-proxy"]?.scalar?.string)
        let corp = proxies.last?["name"]?.scalar?.string
        XCTAssertEqual(corp, CorpProxyInjector.proxyName)
        XCTAssertEqual(proxies.last?["type"]?.scalar?.string, "http")
        XCTAssertEqual(proxies.last?["server"]?.scalar?.string, "10.1.2.3")
        XCTAssertEqual(proxies.last?["port"]?.scalar?.string, "8080")
        // 规则段保持原样
        XCTAssertEqual(document["rules"]?.sequence?.first?.scalar?.string, "MATCH,DIRECT")
    }

    func testProviderGetsOverrideAndFetchProxy() throws {
        let yaml = """
        proxies: []
        proxy-providers:
          jf_link:
            type: http
            url: https://example.com/sub
            interval: 3600
            override:
              additional-prefix: "节点 "
        """
        let injected = try XCTUnwrap(CorpProxyInjector.inject(configYAML: yaml, settings: self.settings))
        let document = try XCTUnwrap(Yams.compose(yaml: injected))
        let provider = document["proxy-providers"]?["jf_link"]
        XCTAssertEqual(provider?["proxy"]?.scalar?.string, CorpProxyInjector.proxyName)
        XCTAssertEqual(provider?["override"]?["dialer-proxy"]?.scalar?.string, CorpProxyInjector.proxyName)
        // 用户已有的 override 字段保留
        XCTAssertEqual(provider?["override"]?["additional-prefix"]?.scalar?.string, "节点 ")
        // corp 节点也进入 proxies（provider 形态配置的 proxies 通常为空数组）
        XCTAssertEqual(document["proxies"]?.sequence?.count, 1)
    }

    func testProviderOnlyConfigCreatesProxiesSection() throws {
        let yaml = """
        proxy-providers:
          jf_link:
            type: http
            url: https://example.com/sub
        """
        let injected = try XCTUnwrap(CorpProxyInjector.inject(configYAML: yaml, settings: self.settings))
        let document = try XCTUnwrap(Yams.compose(yaml: injected))
        XCTAssertEqual(document["proxies"]?.sequence?.count, 1)
        XCTAssertEqual(document["proxies"]?.sequence?.first?["name"]?.scalar?.string, CorpProxyInjector.proxyName)
    }

    func testCredentialsIncludedWhenPresent() throws {
        let yaml = """
        proxies:
          - name: node
            type: ss
            server: s
            port: 1
        """
        let injected = try XCTUnwrap(CorpProxyInjector.inject(configYAML: yaml, settings: self.credentialSettings))
        let document = try XCTUnwrap(Yams.compose(yaml: injected))
        let corp = document["proxies"]?.sequence?.last
        XCTAssertEqual(corp?["username"]?.scalar?.string, "user")
        XCTAssertEqual(corp?["password"]?.scalar?.string, "pass")
    }

    func testExistingDialerProxyIsOverridden() throws {
        let yaml = """
        proxies:
          - name: node
            type: vmess
            server: s
            port: 443
            dialer-proxy: "user-own-proxy"
        """
        let injected = try XCTUnwrap(CorpProxyInjector.inject(configYAML: yaml, settings: self.settings))
        let document = try XCTUnwrap(Yams.compose(yaml: injected))
        XCTAssertEqual(
            document["proxies"]?.sequence?.first?["dialer-proxy"]?.scalar?.string,
            CorpProxyInjector.proxyName)
    }

    func testConfigWithoutProxySectionsReturnsNil() throws {
        let yaml = """
        mixed-port: 7890
        rules:
          - MATCH,DIRECT
        """
        let injected = try CorpProxyInjector.inject(configYAML: yaml, settings: self.settings)
        XCTAssertNil(injected)
        XCTAssertFalse(CorpProxyInjector.needsInjection(configYAML: yaml))
    }

    func testNeedsInjectionDetectsBothSections() {
        XCTAssertTrue(CorpProxyInjector.needsInjection(configYAML: "proxies: []\n"))
        XCTAssertTrue(CorpProxyInjector.needsInjection(configYAML: "proxy-providers:\n  a:\n    type: http\n"))
        XCTAssertFalse(CorpProxyInjector.needsInjection(configYAML: "rules: []\n"))
    }

    func testSettingsValidity() {
        var settings = self.settings
        XCTAssertTrue(settings.isValid)
        XCTAssertTrue(settings.isReady)
        settings.host = "   "
        XCTAssertFalse(settings.isValid)
        XCTAssertFalse(settings.isReady)
        settings.host = "10.0.0.1"
        settings.port = 0
        XCTAssertFalse(settings.isValid)
        settings.port = 65536
        XCTAssertFalse(settings.isValid)
        settings.port = 65535
        XCTAssertTrue(settings.isValid)
        settings.isEnabled = false
        XCTAssertFalse(settings.isReady)
    }
}
