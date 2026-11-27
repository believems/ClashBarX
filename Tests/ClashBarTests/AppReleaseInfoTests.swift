import Foundation
import XCTest
@testable import ClashBar

/// 双平台发版线共用后，更新检查必须按平台资产过滤（回归：Windows 发版较新时
/// /releases/latest 会被 macOS 误报为新版本）。
final class AppReleaseInfoTests: XCTestCase {
    private func decode(_ json: String) throws -> AppReleaseInfo {
        try JSONDecoder().decode(AppReleaseInfo.self, from: Data(json.utf8))
    }

    func testHasMacInstallerRequiresDmgAsset() throws {
        let mac = try self.decode("""
        {"tag_name":"v1.0.1","name":"ClashBarX 1.0.1","html_url":"https://example.com/r",
         "draft":false,"prerelease":false,
         "assets":[{"name":"ClashBarX-1.0.1-apple-silicon.dmg","browser_download_url":"https://example.com/a.dmg"}]}
        """)
        XCTAssertTrue(mac.hasMacInstaller)

        let windows = try self.decode("""
        {"tag_name":"win-v1.0.0","name":"ClashBarX for Windows 1.0.0","html_url":"https://example.com/w",
         "draft":false,"prerelease":false,
         "assets":[{"name":"ClashBarX-1.0.0-setup.exe","browser_download_url":"https://example.com/a.exe"}]}
        """)
        XCTAssertFalse(windows.hasMacInstaller)
    }

    func testEmptyAssetsIsNotMacInstaller() throws {
        let empty = try self.decode("""
        {"tag_name":"v1.0.0","name":"n","html_url":"https://example.com",
         "draft":false,"prerelease":false,"assets":[]}
        """)
        XCTAssertFalse(empty.hasMacInstaller)
    }
}
