import AppKit
import SwiftUI

private typealias FS = MenuBarLayoutTokens.FontSize

extension Font {
    /// UI text: SF Pro, matching native macOS controls.
    static func app(size: CGFloat, weight: Font.Weight = .regular) -> Font {
        .system(size: size, weight: weight)
    }

    /// Code-like content (logs, addresses) where column alignment matters.
    static func appMono(size: CGFloat, weight: Font.Weight = .regular) -> Font {
        Font(NSFont.monospacedSystemFont(ofSize: size, weight: weight.nsFontWeight))
    }
}

extension Font.Weight {
    fileprivate var nsFontWeight: NSFont.Weight {
        switch self {
        case .ultraLight:
            .ultraLight
        case .thin:
            .thin
        case .light:
            .light
        case .regular:
            .regular
        case .medium:
            .medium
        case .semibold:
            .semibold
        case .bold:
            .bold
        case .heavy:
            .heavy
        case .black:
            .black
        default:
            .regular
        }
    }
}
