import Foundation

enum AppReleaseServiceError: Error {
    case invalidResponse
    case statusCode(Int)
}

enum AppReleaseService {
    /// 用列表端点而非 /releases/latest：latest 只排除 prerelease/draft，不区分平台——
    /// Windows 发版（win-v*，含 exe/zip 资产）较新时会被误报为 macOS 的新版本。
    /// 这里按 DMG 资产过滤出 macOS 可用的最新稳定发版。
    private static let releasesURL = URL(string: "https://api.github.com/repos/believems/ClashBarX/releases?per_page=10")!

    static func fetchLatestRelease(currentVersion: String) async throws -> AppReleaseInfo {
        var request = URLRequest(url: Self.releasesURL)
        request.timeoutInterval = 8
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
        request.setValue("ClashBar/\(currentVersion)", forHTTPHeaderField: "User-Agent")

        let session = Self.makeSession()
        let (data, response) = try await session.data(for: request)
        guard let httpResponse = response as? HTTPURLResponse else {
            throw AppReleaseServiceError.invalidResponse
        }
        guard 200..<300 ~= httpResponse.statusCode else {
            throw AppReleaseServiceError.statusCode(httpResponse.statusCode)
        }

        let releases = try JSONDecoder().decode([AppReleaseInfo].self, from: data)
        guard let latest = releases.first(where: { !$0.isDraft && !$0.isPrerelease && $0.hasMacInstaller }) else {
            throw AppReleaseServiceError.invalidResponse
        }
        return latest
    }

    private static func makeSession() -> URLSession {
        URLSessionFactory.makeEphemeralSession(options: .init(
            timeoutIntervalForRequest: 8,
            timeoutIntervalForResource: 15))
    }
}
