import XCTest
@testable import ClashBar

@MainActor
final class LogsViewModelTests: XCTestCase {
    private func makeLogs(_ count: Int, message: (Int) -> String = { "log \($0)" }) -> [AppErrorLogEntry] {
        (0..<count).map { index in
            AppErrorLogEntry(source: .clashbar, level: "info", message: message(index))
        }
    }

    private func update(_ viewModel: LogsViewModel, logs: [AppErrorLogEntry]) {
        viewModel.updateVisibleLogs(
            from: logs,
            searchTextContent: { $0.message },
            normalizedLevel: { raw in raw.uppercased() },
            levelFilter: { level in
                switch level {
                case "ERROR": .error
                case "WARNING": .warning
                default: .info
                }
            })
    }

    func testRenderIsCapped() {
        let viewModel = LogsViewModel()
        self.update(viewModel, logs: self.makeLogs(500))

        XCTAssertEqual(viewModel.visibleLogs.count, 120)
        XCTAssertEqual(viewModel.matchedCount, 500)
    }

    /// Regression: search used to scan only the newest 120 entries, so older
    /// matches were unreachable even though the badge reported the full total.
    func testSearchMatchesEntriesBeyondRenderCap() {
        let viewModel = LogsViewModel()
        let logs = self.makeLogs(400) { $0 == 3 ? "needle-at-index-3" : "noise \($0)" }
        viewModel.searchText = "needle-at-index-3"
        self.update(viewModel, logs: logs)

        XCTAssertEqual(viewModel.matchedCount, 1)
        XCTAssertEqual(viewModel.visibleLogs.first?.message, "needle-at-index-3")
    }

    func testSourceAndLevelFiltersCombine() {
        let viewModel = LogsViewModel()
        let logs = [
            AppErrorLogEntry(source: .clashbar, level: "error", message: "a"),
            AppErrorLogEntry(source: .mihomo, level: "error", message: "b"),
            AppErrorLogEntry(source: .clashbar, level: "info", message: "c"),
        ]

        viewModel.selectedSources = [.clashbar]
        viewModel.selectedLevels = [.error]
        self.update(viewModel, logs: logs)

        XCTAssertEqual(viewModel.visibleLogs.map(\.message), ["a"])
        XCTAssertEqual(viewModel.matchedCount, 1)
    }

    func testEmptySelectionShowsEverything() {
        let viewModel = LogsViewModel()
        self.update(viewModel, logs: self.makeLogs(5))

        XCTAssertEqual(viewModel.matchedCount, 5)
        XCTAssertEqual(viewModel.visibleLogs.count, 5)
    }
}
