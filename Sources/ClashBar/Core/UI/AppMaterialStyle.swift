import SwiftUI

enum AppSurfaceFallbackStyle {
    case material(Material)
    case color(Color)
}

struct AppMaterialSurface: View {
    let cornerRadius: CGFloat
    let fallbackStyle: AppSurfaceFallbackStyle
    let stroke: Color
    var lineWidth: CGFloat = MenuBarLayoutTokens.stroke

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: self.cornerRadius, style: .continuous)

        Group {
            switch self.fallbackStyle {
            case let .material(material):
                shape.fill(material)
            case let .color(color):
                shape.fill(color)
            }
        }
        .overlay {
            shape.stroke(self.stroke, lineWidth: self.lineWidth)
        }
    }
}

extension View {
    /// Root panel surface. On macOS 26+ the window itself is an `NSGlassEffectView`
    /// (see `PanelGlassContainerController`), so only older systems need a SwiftUI background.
    @ViewBuilder
    func rootPanelSurface(cornerRadius: CGFloat, stroke: Color) -> some View {
        if #available(macOS 26.0, *) {
            self
        } else {
            self.panelSurface(cornerRadius: cornerRadius, stroke: stroke)
        }
    }

    /// Floating panel surface: Liquid Glass on macOS 26+, translucent material before that.
    @ViewBuilder
    func panelSurface(cornerRadius: CGFloat, stroke: Color) -> some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
        if #available(macOS 26.0, *) {
            self
                .glassEffect(.regular, in: shape)
                .overlay(shape.strokeBorder(stroke.opacity(0.5), lineWidth: MenuBarLayoutTokens.stroke))
        } else {
            self.background(
                AppMaterialSurface(
                    cornerRadius: cornerRadius,
                    fallbackStyle: .material(.regularMaterial),
                    stroke: stroke))
        }
    }

    @ViewBuilder
    func appBorderedButtonStyle(prominent: Bool = false) -> some View {
        if prominent {
            self.buttonStyle(.borderedProminent)
        } else {
            // Native .bordered renders as a bright gray block on the dark glass panel; a faint
            // neutral surface matches the panel's other controls.
            self.buttonStyle(PanelButtonStyle())
        }
    }
}

protocol TranslatingView: View {
    var appViewModel: AppViewModel { get }
}

extension TranslatingView {
    var language: AppLanguage {
        appViewModel.uiLanguage
    }

    func tr(_ key: String) -> String {
        L10n.t(key, language: self.language)
    }

    func tr(_ key: String, _ args: CVarArg...) -> String {
        L10n.t(key, language: self.language, args: args)
    }
}
