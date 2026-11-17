import Foundation
import SwiftUI

@MainActor
final class LogsViewModel: ObservableObject {
    @Published var selectedSources: Set<AppLogSource> = []
    @Published var selectedLevels: Set<LogLevelFilter> = []
    @Published var searchText: String = ""
    @Published private(set) var visibleLogs: [AppErrorLogEntry] = []
    @Published private(set) var matchedCount: Int = 0

    private static let renderLimit = 120

    init() {}

    func toggleSource(_ source: AppLogSource) {
        self.toggleSelection(source, selection: &self.selectedSources)
    }

    func toggleLevel(_ level: LogLevelFilter) {
        self.toggleSelection(level, selection: &self.selectedLevels)
    }

    func updateVisibleLogs(
        from logs: [AppErrorLogEntry],
        searchTextContent: @escaping (AppErrorLogEntry) -> String,
        normalizedLevel: @escaping (String) -> String,
        levelFilter: @escaping (String) -> LogLevelFilter)
    {
        let trimmedKeyword = self.searchText.trimmed
        let isShowingAllSources = self.selectedSources.isEmpty
        let isShowingAllLevels = self.selectedLevels.isEmpty
        let isShowingAll = trimmedKeyword.isEmpty && isShowingAllSources && isShowingAllLevels

        let matched: [AppErrorLogEntry] = if isShowingAll {
            logs
        } else {
            logs.filter { log in
                guard isShowingAllSources || self.selectedSources.contains(log.source) else { return false }
                guard trimmedKeyword.isEmpty || searchTextContent(log).localizedStandardContains(trimmedKeyword)
                else {
                    return false
                }
                return isShowingAllLevels || self.selectedLevels
                    .contains(levelFilter(normalizedLevel(log.level)))
            }
        }

        let nextLogs = Array(matched.prefix(Self.renderLimit))

        if self.matchedCount != matched.count {
            self.matchedCount = matched.count
        }
        guard nextLogs != self.visibleLogs else { return }
        self.visibleLogs = nextLogs
    }

    private func toggleSelection<Value: Hashable>(
        _ value: Value,
        selection: inout Set<Value>)
    {
        if selection.contains(value) {
            selection.remove(value)
        } else {
            selection.insert(value)
        }
    }
}
