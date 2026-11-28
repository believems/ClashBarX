import Foundation

/// 内网前置代理（Issue #2）：集团内网仅允许经企业 HTTP 代理出外网时，
/// 在配置组装链末段注入 dialer-proxy，节点连接与订阅拉取统一经企业代理拨出。
/// 注入只发生在生成给内核的运行时配置上，订阅源文件保持原样；
/// 订阅更新/切换/自愈都走同一条组装管道，注入自动保持。
/// 存储属性（@AppStorage 群）在 AppViewModel 本体，此处只做组装与生效。
@MainActor
extension AppViewModel {
    var corpProxySettings: CorpProxySettings {
        CorpProxySettings(
            isEnabled: self.corpProxyEnabled,
            host: self.corpProxyHost,
            port: self.corpProxyPort,
            username: self.corpProxyUsername,
            password: self.corpProxyPassword)
    }

    // MARK: - Launch 注入

    /// 对覆写链产物做前置代理注入：启用且有效时生成注入版文件（state/corp/）。
    /// 注入失败降级原样并记日志——不阻塞内核启动（家用环境误开时有明确报错可查）。
    func applyCorpProxyToLaunch(_ launch: CoreLaunchConfig) -> CoreLaunchConfig {
        let settings = self.corpProxySettings
        guard settings.isReady else { return launch }
        guard let raw = try? String(contentsOfFile: launch.path, encoding: .utf8) else {
            self.appendLog(level: "error", message: self.tr("log.corpproxy.unreadable"))
            return launch
        }

        do {
            guard let injected = try CorpProxyInjector.inject(configYAML: raw, settings: settings) else {
                return launch
            }
            let directory = self.workingDirectoryManager.stateDirectoryURL
                .appendingPathComponent("corp", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let fileName = URL(fileURLWithPath: launch.path).lastPathComponent
            let target = directory.appendingPathComponent("runtime-\(fileName)")
            try Data(injected.utf8).write(to: target, options: .atomic)
            self.appendLog(
                level: "info",
                message: self.tr("log.corpproxy.applied", settings.trimmedHost, settings.port))
            return CoreLaunchConfig(
                path: target.path,
                workingDirectory: launch.workingDirectory,
                overrideContent: launch.overrideContent,
                isCorpInjected: true)
        } catch {
            self.appendLog(level: "error", message: self.tr("log.corpproxy.failed", "\(error)"))
            return launch
        }
    }

    /// 设置变更后让内核立即重载（配置组装链会重新注入）；内核未运行时下次启动自动生效。
    func reloadForCorpProxyChange() async {
        guard self.isRuntimeRunning, !self.isRemoteTarget else { return }
        guard let configPath = await self.resolveSelectedConfigPath() else { return }
        do {
            try await self.putSelectedConfig(configPath: configPath)
        } catch {
            self.appendLog(level: "error", message: self.tr("log.corpproxy.reload_failed", "\(error)"))
        }
    }

    // MARK: - 订阅下载代理（决策点 3：App 层拉取同样经企业代理）

    /// 订阅下载请求的代理端点；未启用或配置无效时为 nil（走系统默认）。
    var corpProxySubscriptionEndpoint: CorpProxyEndpoint? {
        let settings = self.corpProxySettings
        guard settings.isReady else { return nil }
        return CorpProxyEndpoint(host: settings.trimmedHost, port: settings.port)
    }
}
