import AppKit

/// `--proxy-guard` 命令行入口：面向自动化（如大模型发现 GitHub 访问异常时）触发一次代理守护检查。
///
/// - 已有运行实例：经分布式通知让运行中的实例立即探测并按需恢复，随后本进程直接退出。
/// - 无运行实例：放行正常启动流程，应用启动后的首个探测周期会立即执行一次
///   （见 `AppViewModel+ProxyGuard.startProxyGuardIfNeeded`）。
enum ProxyGuardCLI {
    static func runIfNeeded() {
        guard ProcessInfo.processInfo.arguments.contains(ProxyGuardConstants.cliTriggerArgument) else { return }

        let currentPID = ProcessInfo.processInfo.processIdentifier
        let otherInstance = NSRunningApplication.runningApplications(withBundleIdentifier: "com.clashbar")
            .first { $0.processIdentifier != currentPID }

        if otherInstance != nil {
            DistributedNotificationCenter.default().postNotificationName(
                Notification.Name(ProxyGuardConstants.triggerNotificationName),
                object: nil,
                userInfo: nil,
                deliverImmediately: true)
            exit(0)
        }
        // 无实例：继续正常启动，启动后自动执行一次探测。
    }
}
