import Foundation

struct SubscriptionUsage: Codable, Equatable {
    var upload: Int64?
    var download: Int64?
    var total: Int64?
    var expiresAt: Date?

    static func parse(_ header: String?) -> SubscriptionUsage? {
        guard let header else { return nil }
        var values: [String: Int64] = [:]
        for part in header.split(separator: ";") {
            let pair = part.split(separator: "=", maxSplits: 1)
            guard pair.count == 2, let number = Int64(pair[1].trimmingCharacters(in: .whitespaces)), number >= 0 else { continue }
            values[pair[0].trimmingCharacters(in: .whitespaces).lowercased()] = number
        }
        guard !values.isEmpty else { return nil }
        return SubscriptionUsage(upload: values["upload"], download: values["download"], total: values["total"],
                                 expiresAt: values["expire"].flatMap { $0 > 0 ? Date(timeIntervalSince1970: TimeInterval($0)) : nil })
    }

    var used: Int64? {
        guard let upload, let download else { return nil }
        let (value, overflow) = upload.addingReportingOverflow(download)
        return overflow ? nil : value
    }
}

/// 订阅元数据：融合 flowke 的 `displayName` / `usage` / `enabled`（订阅信息展示与开关）
/// 与 Gongsc 的 `lastSuccessAt` / `nextRetryAt`（更新成功时间与失败退避重试）。
struct RemoteConfigSubscription: Codable, Equatable {
    let urlString: String
    /// A human-readable name discovered from the remote document. The file name
    /// remains the stable storage key, so changing this metadata never breaks
    /// config references.
    var displayName: String?
    var autoUpdateEnabled: Bool
    var autoUpdateIntervalHours: Int
    var lastUpdateCheckAt: Date?
    var lastSuccessAt: Date?
    var nextRetryAt: Date?
    var usage: SubscriptionUsage?
    var enabled: Bool?
    var isEnabled: Bool { enabled ?? true }

    static let defaultAutoUpdateIntervalHours = 6
    static let minimumAutoUpdateIntervalHours = 1
    static let failureRetryBackoffSeconds: TimeInterval = 15 * 60

    init(
        urlString: String,
        displayName: String? = nil,
        autoUpdateEnabled: Bool = false,
        autoUpdateIntervalHours: Int = defaultAutoUpdateIntervalHours,
        lastUpdateCheckAt: Date? = nil,
        lastSuccessAt: Date? = nil,
        nextRetryAt: Date? = nil,
        usage: SubscriptionUsage? = nil,
        enabled: Bool? = nil)
    {
        self.urlString = urlString
        self.displayName = displayName
        self.autoUpdateEnabled = autoUpdateEnabled
        self.autoUpdateIntervalHours = max(Self.minimumAutoUpdateIntervalHours, autoUpdateIntervalHours)
        self.lastUpdateCheckAt = lastUpdateCheckAt
        self.lastSuccessAt = lastSuccessAt
        self.nextRetryAt = nextRetryAt
        self.usage = usage
        self.enabled = enabled
    }

    func nextUpdateAt() -> Date? {
        guard self.isEnabled, self.autoUpdateEnabled else { return nil }
        if let retryAt = self.nextRetryAt {
            return retryAt
        }
        guard let lastCheck = self.lastUpdateCheckAt else { return nil }
        return self.nextUpdateDate(from: lastCheck)
    }

    func isDue(now: Date = Date()) -> Bool {
        guard self.isEnabled, self.autoUpdateEnabled else { return false }
        guard let nextUpdateAt = self.nextUpdateAt() else { return true }
        return now >= nextUpdateAt
    }

    func markChecked(at date: Date = Date()) -> RemoteConfigSubscription {
        RemoteConfigSubscription(
            urlString: self.urlString,
            displayName: self.displayName,
            autoUpdateEnabled: self.autoUpdateEnabled,
            autoUpdateIntervalHours: self.autoUpdateIntervalHours,
            lastUpdateCheckAt: date,
            lastSuccessAt: self.lastSuccessAt,
            nextRetryAt: self.nextRetryAt,
            usage: self.usage,
            enabled: self.enabled)
    }

    func markSucceeded(at date: Date = Date()) -> RemoteConfigSubscription {
        RemoteConfigSubscription(
            urlString: self.urlString,
            displayName: self.displayName,
            autoUpdateEnabled: self.autoUpdateEnabled,
            autoUpdateIntervalHours: self.autoUpdateIntervalHours,
            lastUpdateCheckAt: date,
            lastSuccessAt: date,
            nextRetryAt: nil,
            usage: self.usage,
            enabled: self.enabled)
    }

    func markFailed(at date: Date = Date(), isAutoUpdate: Bool) -> RemoteConfigSubscription {
        let shouldScheduleRetry = self.autoUpdateEnabled && (isAutoUpdate || self.isDue(now: date))
        let retryInterval = min(Self.failureRetryBackoffSeconds, TimeInterval(self.autoUpdateIntervalHours) * 3600)
        return RemoteConfigSubscription(
            urlString: self.urlString,
            displayName: self.displayName,
            autoUpdateEnabled: self.autoUpdateEnabled,
            autoUpdateIntervalHours: self.autoUpdateIntervalHours,
            lastUpdateCheckAt: self.lastUpdateCheckAt,
            lastSuccessAt: self.lastSuccessAt,
            nextRetryAt: shouldScheduleRetry ? date.addingTimeInterval(retryInterval) : self.nextRetryAt,
            usage: self.usage,
            enabled: self.enabled)
    }

    private func nextUpdateDate(from lastCheck: Date) -> Date {
        lastCheck.addingTimeInterval(TimeInterval(self.autoUpdateIntervalHours) * 3600)
    }
}
