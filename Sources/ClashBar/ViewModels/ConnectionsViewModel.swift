import Foundation
import SwiftUI

enum ConnectionsTransportFilter: String, CaseIterable, Identifiable {
    case all
    case tcp
    case udp
    case other

    var id: String {
        rawValue
    }

    var titleKey: String {
        switch self {
        case .all:
            "ui.network.filter.transport.all"
        case .tcp:
            "ui.network.filter.transport.tcp"
        case .udp:
            "ui.network.filter.transport.udp"
        case .other:
            "ui.network.filter.transport.other"
        }
    }

    func matches(_ network: String?) -> Bool {
        let normalized = network.trimmedOrEmpty.lowercased()

        switch self {
        case .all:
            return true
        case .tcp:
            return normalized == "tcp"
        case .udp:
            return normalized == "udp"
        case .other:
            return !normalized.isEmpty && normalized != "tcp" && normalized != "udp"
        }
    }
}

enum ConnectionsSortKey: String, CaseIterable, Identifiable {
    case `default`
    case startTime
    case downloadSpeed
    case uploadSpeed
    case download
    case upload
    case total

    var id: String {
        rawValue
    }

    var titleKey: String {
        switch self {
        case .default:
            "ui.network.sort.key.default"
        case .startTime:
            "ui.network.sort.key.start_time"
        case .downloadSpeed:
            "ui.network.sort.key.download_speed"
        case .uploadSpeed:
            "ui.network.sort.key.upload_speed"
        case .download:
            "ui.network.sort.key.download"
        case .upload:
            "ui.network.sort.key.upload"
        case .total:
            "ui.network.sort.key.total"
        }
    }

    /// `.default` keeps the core's order, so a direction does not apply to it.
    var supportsDirection: Bool {
        self != .default
    }

    var isSpeed: Bool {
        self == .downloadSpeed || self == .uploadSpeed
    }

    /// Maps the pre-0.4.1 combined sort option (`clashbar.connections.sort_option`).
    static func migrated(fromLegacyOption raw: String) -> (key: ConnectionsSortKey, direction: ConnectionsSortDirection)? {
        switch raw {
        case "default": (.default, .descending)
        case "newest": (.startTime, .descending)
        case "oldest": (.startTime, .ascending)
        case "uploadDesc": (.upload, .descending)
        case "downloadDesc": (.download, .descending)
        case "totalDesc": (.total, .descending)
        default: nil
        }
    }
}

enum ConnectionsSortDirection: String, CaseIterable {
    case descending
    case ascending

    var toggled: ConnectionsSortDirection {
        self == .descending ? .ascending : .descending
    }
}

/// Per-connection transfer rate in bytes per second.
struct ConnectionSpeed: Equatable {
    var upload: Double
    var download: Double

    static let zero = ConnectionSpeed(upload: 0, download: 0)
}

@MainActor
final class ConnectionsViewModel: ObservableObject {
    @Published var filterText: String = ""
    @Published var transportFilter: ConnectionsTransportFilter = .all
    @Published var sortKey: ConnectionsSortKey = .default
    @Published var sortDirection: ConnectionsSortDirection = .descending
    @Published var hoveredConnectionID: String?
    /// While the pointer is over the list, rows keep their positions so the target does not jump away.
    @Published var isReorderingPaused = false
    @Published private(set) var visibleConnections: [ConnectionSummary] = []
    @Published private(set) var speeds: [String: ConnectionSpeed] = [:]

    private struct TrafficSample {
        let upload: Int64
        let download: Int64
        let time: TimeInterval
        let speed: ConnectionSpeed
    }

    private var samples: [String: TrafficSample] = [:]

    /// Weight of the newest measurement; the rest carries over so ranks do not swap on one-second spikes.
    private static let speedSmoothing = 0.5
    private static let minimumSampleInterval: TimeInterval = 0.2

    init() {}

    /// Derives speeds from consecutive cumulative snapshots, as mihomo reports only byte totals per
    /// connection. Divides by the real elapsed time because the stream interval is not guaranteed.
    func ingestSnapshot(_ connections: [ConnectionSummary], now: TimeInterval = ProcessInfo.processInfo.systemUptime) {
        var nextSamples: [String: TrafficSample] = [:]
        var nextSpeeds: [String: ConnectionSpeed] = [:]
        nextSamples.reserveCapacity(connections.count)

        for connection in connections {
            let upload = connection.upload ?? 0
            let download = connection.download ?? 0
            var speed = ConnectionSpeed.zero

            if let previous = self.samples[connection.id] {
                let elapsed = now - previous.time
                guard elapsed >= Self.minimumSampleInterval else {
                    nextSamples[connection.id] = previous
                    nextSpeeds[connection.id] = previous.speed
                    continue
                }
                let weight = Self.speedSmoothing
                speed = ConnectionSpeed(
                    upload: weight * Double(max(0, upload - previous.upload)) / elapsed
                        + (1 - weight) * previous.speed.upload,
                    download: weight * Double(max(0, download - previous.download)) / elapsed
                        + (1 - weight) * previous.speed.download)
            }

            nextSamples[connection.id] = TrafficSample(upload: upload, download: download, time: now, speed: speed)
            nextSpeeds[connection.id] = speed
        }

        self.samples = nextSamples
        if nextSpeeds != self.speeds {
            self.speeds = nextSpeeds
        }
    }

    func speed(for id: String) -> ConnectionSpeed {
        self.speeds[id] ?? .zero
    }

    func updateVisibleConnections(
        from connections: [ConnectionSummary],
        searchText: (ConnectionSummary) -> String)
    {
        let source = connections.prefix(120)
        let keyword = self.filterText.trimmed

        let filtered: [ConnectionSummary] = if keyword.isEmpty, self.transportFilter == .all {
            Array(source)
        } else {
            source.filter { connection in
                guard self.transportFilter.matches(connection.metadata?.network) else { return false }
                guard keyword.isEmpty || searchText(connection).localizedStandardContains(keyword) else {
                    return false
                }
                return true
            }
        }

        var nextConnections = self.sortedConnections(filtered)
        if self.isReorderingPaused {
            nextConnections = self.preservingCurrentOrder(nextConnections)
        }
        guard nextConnections != self.visibleConnections else { return }
        self.visibleConnections = nextConnections
    }

    private func sortedConnections(_ source: [ConnectionSummary]) -> [ConnectionSummary] {
        guard let metric = self.sortMetric(for: self.sortKey) else { return source }
        let descending = self.sortDirection == .descending

        return source.sorted { lhs, rhs in
            let left = metric(lhs)
            let right = metric(rhs)
            if left != right {
                return descending ? left > right : left < right
            }
            // Snapshots may list connections in any order; a fixed tie-break keeps equal rows still.
            return lhs.id < rhs.id
        }
    }

    private func sortMetric(for key: ConnectionsSortKey) -> ((ConnectionSummary) -> Double)? {
        switch key {
        case .default:
            nil
        case .startTime:
            { $0.startTimestamp ?? 0 }
        case .downloadSpeed:
            { self.speed(for: $0.id).download }
        case .uploadSpeed:
            { self.speed(for: $0.id).upload }
        case .download:
            { Double($0.download ?? 0) }
        case .upload:
            { Double($0.upload ?? 0) }
        case .total:
            { Double(($0.upload ?? 0) + ($0.download ?? 0)) }
        }
    }

    /// Rows already on screen stay where they are (with fresh data); new rows join at the end.
    private func preservingCurrentOrder(_ sorted: [ConnectionSummary]) -> [ConnectionSummary] {
        let currentIndex = Dictionary(
            self.visibleConnections.enumerated().map { ($1.id, $0) },
            uniquingKeysWith: { first, _ in first })
        let kept = sorted
            .filter { currentIndex[$0.id] != nil }
            .sorted { currentIndex[$0.id, default: 0] < currentIndex[$1.id, default: 0] }
        let added = sorted.filter { currentIndex[$0.id] == nil }
        return kept + added
    }
}
