import AppKit
import Foundation

/// 自动代理守护：周期性经当前代理端口探测外网连通性，连续失败后按
/// 「组内切换 → 订阅自愈」的顺序恢复（BOSS 2026-11-24 定档）。
///
/// - 探测：经本地混合端口对 204 URL 发 HEAD 请求，非 2xx / 超时 / 错误均视为不可用。
/// - 恢复：对手动选择（Selector）组逐组测速，当前节点不可用而组内有可用节点时切换；
///   全部组都无可用节点时更新全部代理提供者（订阅自愈）后重测再切换。
/// - CLI：`--proxy-guard` 经分布式通知触发运行中实例立即探测；无实例时随应用启动触发。

/// 最近一次探测的结果，供设置页反馈行显示。
enum ProxyGuardProbeOutcome: Equatable {
    case reachable
    case failed(consecutiveCount: Int)
}

/// 跨隔离域使用的守护常量（非隔离，供 CLI 与 nonisolated 探测函数引用）。
enum ProxyGuardConstants {
    static let failureThreshold = 3
    static let probeTimeout: TimeInterval = 6
    static let triggerNotificationName = "com.clashbarx.proxyguard.trigger"
    static let cliTriggerArgument = "--proxy-guard"
}

@MainActor
extension AppViewModel {
    static let proxyGuardFailureThreshold = ProxyGuardConstants.failureThreshold
    static let proxyGuardTriggerNotificationName = ProxyGuardConstants.triggerNotificationName
    static let cliTriggerArgument = ProxyGuardConstants.cliTriggerArgument

    var proxyGuardProbeURL: URL? { URL(string: defaultHealthcheckURL) }

    // MARK: - 生命周期

    /// App 启动与设置开关打开时调用；幂等。
    func startProxyGuardIfNeeded() {
        self.registerProxyGuardTriggerObserver()
        guard self.proxyGuardEnabled, self.proxyGuardTask == nil else { return }
        self.proxyGuardConsecutiveFailures = 0
        self.proxyGuardTask = Task { [weak self] in
            // CLI 触发的首启：先立即探一次再进入周期循环。
            if ProcessInfo.processInfo.arguments.contains(Self.cliTriggerArgument) {
                await self?.proxyGuardProbe()
                guard let self, !Task.isCancelled, self.proxyGuardEnabled else { return }
            }
            while !Task.isCancelled {
                let interval = max(30, self?.proxyGuardIntervalSeconds ?? 300)
                do { try await Task.sleep(nanoseconds: UInt64(interval) * 1_000_000_000) } catch { return }
                guard let self, !Task.isCancelled, self.proxyGuardEnabled else { return }
                await self.proxyGuardProbe()
            }
        }
        self.appendLog(level: "info", message: self.tr("app.proxyguard.started", self.proxyGuardIntervalSeconds))
    }

    func stopProxyGuard() {
        self.proxyGuardTask?.cancel()
        self.proxyGuardTask = nil
        self.proxyGuardConsecutiveFailures = 0
    }

    func shutdownProxyGuard() {
        self.stopProxyGuard()
        if let observer = self.proxyGuardTriggerObserver {
            DistributedNotificationCenter.default().removeObserver(observer)
            self.proxyGuardTriggerObserver = nil
        }
    }

    // MARK: - 探测

    /// 经当前混合端口做一次连通性探测并推进失败计数。
    func proxyGuardProbe() async {
        guard self.isRuntimeRunning, !self.isRemoteTarget else { return }
        guard let url = self.proxyGuardProbeURL, let port = self.mixedProxyPort, port > 0 else { return }

        self.isProxyGuardProbing = true
        defer { self.isProxyGuardProbing = false }

        if await Self.probeConnectivity(url: url, proxyPort: port) {
            if self.proxyGuardConsecutiveFailures > 0 {
                self.appendLog(level: "info", message: self.tr("app.proxyguard.recovered"))
            }
            self.proxyGuardConsecutiveFailures = 0
            self.proxyGuardLastProbeOutcome = .reachable
            return
        }

        self.proxyGuardConsecutiveFailures += 1
        self.proxyGuardLastProbeOutcome = .failed(consecutiveCount: self.proxyGuardConsecutiveFailures)
        self.appendLog(
            level: "warning",
            message: self.tr(
                "app.proxyguard.probe_failed",
                self.proxyGuardConsecutiveFailures, Self.proxyGuardFailureThreshold))

        if self.proxyGuardConsecutiveFailures >= Self.proxyGuardFailureThreshold {
            self.proxyGuardConsecutiveFailures = 0
            await self.proxyGuardRecover()
        }
    }

    /// 经本地代理端口对 204 URL 发 HEAD 请求；非 2xx / 超时 / 错误均视为不可用。
    nonisolated static func probeConnectivity(url: URL, proxyPort: Int) async -> Bool {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = ProxyGuardConstants.probeTimeout
        config.timeoutIntervalForResource = ProxyGuardConstants.probeTimeout + 2
        config.connectionProxyDictionary = [
            kCFNetworkProxiesHTTPEnable: true,
            kCFNetworkProxiesHTTPProxy: "127.0.0.1",
            kCFNetworkProxiesHTTPPort: proxyPort,
        ]
        let session = URLSession(configuration: config)
        defer { session.finishTasksAndInvalidate() }
        var request = URLRequest(url: url)
        request.httpMethod = "HEAD"
        do {
            let (_, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { return false }
            return (200..<300).contains(http.statusCode)
        } catch {
            return false
        }
    }

    // MARK: - 恢复（组内切换 + 订阅自愈）

    func proxyGuardRecover() async {
        guard !self.proxyGuardRecoveryInFlight else { return }
        self.proxyGuardRecoveryInFlight = true
        defer { self.proxyGuardRecoveryInFlight = false }

        self.appendLog(level: "warning", message: self.tr("app.proxyguard.recovering"))

        // 1) 组内切换：当前节点坏、组内有活节点就直接切。
        if await self.recoverBySwitchingWithinGroups(), await self.proxyGuardStillReachable() {
            self.appendLog(level: "info", message: self.tr("app.proxyguard.recovered"))
            return
        }

        // 2) 订阅自愈：全部组都无可用节点时更新全部提供者，等待内核刷新后重测再切换。
        await self.updateAllProxyProvidersForRecovery()
        try? await Task.sleep(nanoseconds: 3_000_000_000)
        await self.refreshProxyGroups()

        if await self.recoverBySwitchingWithinGroups(), await self.proxyGuardStillReachable() {
            self.appendLog(level: "info", message: self.tr("app.proxyguard.recovered"))
        } else {
            self.appendLog(level: "error", message: self.tr("app.proxyguard.recovery_failed"))
        }
    }

    private func proxyGuardStillReachable() async -> Bool {
        guard let url = self.proxyGuardProbeURL, let port = self.mixedProxyPort, port > 0 else { return false }
        return await Self.probeConnectivity(url: url, proxyPort: port)
    }

    /// 对每个手动选择组：组测速，当前节点不可用而组内有可用节点时切换到组内最快节点。
    private func recoverBySwitchingWithinGroups() async -> Bool {
        var didSwitch = false
        let selectorGroups = self.proxyGroups
            .filter { ($0.type ?? "").lowercased().contains("selector") }
        for group in selectorGroups {
            guard let results = await self.measureGroupDelays(group) else { continue }
            guard let current = group.now, !current.isEmpty else { continue }
            if results[current].map({ $0 > 0 }) == true { continue }
            guard let best = results
                .filter({ $0.value > 0 && $0.key != current })
                .min(by: { $0.value < $1.value })
            else { continue }
            await self.switchProxy(group: group.name, target: best.key)
            self.appendLog(
                level: "info",
                message: self.tr("app.proxyguard.switched", group.name, best.key))
            didSwitch = true
        }
        return didSwitch
    }

    /// 直接向内核请求组测速结果，不经过 UI 延迟状态。
    private func measureGroupDelays(_ group: ProxyGroup) async -> [String: Int]? {
        let testURL = group.testUrl?.trimmedNonEmpty ?? defaultHealthcheckURL
        let timeout = group.timeout.flatMap { $0 > 0 ? $0 : nil } ?? defaultHealthcheckTimeoutMilliseconds
        do {
            let response: GroupDelayMeasurement = try await self.clientOrThrow().request(
                .groupDelay(name: group.name, url: testURL, timeout: timeout))
            return response.values
        } catch {
            return nil
        }
    }

    private func updateAllProxyProvidersForRecovery() async {
        let names = self.proxyStore.proxyProvidersDetail.keys.sorted()
        guard !names.isEmpty else { return }
        self.appendLog(level: "info", message: self.tr("app.proxyguard.updating_providers", names.count))
        for name in names {
            await self.updateProxyProvider(name: name)
        }
    }

    // MARK: - CLI 触发（分布式通知）

    private func registerProxyGuardTriggerObserver() {
        guard self.proxyGuardTriggerObserver == nil else { return }
        self.proxyGuardTriggerObserver = DistributedNotificationCenter.default().addObserver(
            forName: Notification.Name(Self.proxyGuardTriggerNotificationName),
            object: nil,
            queue: .main)
        { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.isRuntimeRunning else { return }
                Task { await self.proxyGuardProbe() }
            }
        }
    }
}
