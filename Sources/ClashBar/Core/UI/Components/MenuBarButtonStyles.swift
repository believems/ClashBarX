import SwiftUI

// swiftlint:disable:next type_name
private typealias T = MenuBarLayoutTokens

/// Shared fills for interactive surfaces. Every clickable element moves through
/// the same rest → hover → pressed ladder so affordance and feedback stay consistent.
enum InteractiveFill {
    static func neutral(hovered: Bool, pressed: Bool, resting: Double = 0) -> Color {
        let opacity = if pressed {
            0.16
        } else if hovered {
            0.09
        } else {
            resting
        }
        return Color.primary.opacity(opacity)
    }

    static func accent(hovered: Bool, pressed: Bool) -> Color {
        Color(nsColor: .controlAccentColor).opacity(pressed ? 0.30 : (hovered ? 0.22 : 0.16))
    }
}

/// Square icon button with a visible resting surface, hover highlight and pressed state.
/// `isOn` renders the toggled appearance (accent tint) for buttons that represent a state.
struct IconButtonStyle: ButtonStyle {
    var size: CGFloat = 26
    var isOn: Bool = false
    var tint: Color?
    var destructive: Bool = false
    var showsRestingFill: Bool = true

    func makeBody(configuration: Configuration) -> some View {
        IconButtonBody(configuration: configuration, style: self)
    }

    private struct IconButtonBody: View {
        let configuration: ButtonStyleConfiguration
        let style: IconButtonStyle

        @Environment(\.isEnabled) private var isEnabled
        @State private var hovered = false

        var body: some View {
            let pressed = self.configuration.isPressed
            let shape = RoundedRectangle(cornerRadius: max(6, self.style.size * 0.28), style: .continuous)

            self.configuration.label
                .font(.system(size: self.style.size * 0.48, weight: .medium))
                .foregroundStyle(self.foreground)
                .frame(width: self.style.size, height: self.style.size)
                .background(shape.fill(self.fill(pressed: pressed)))
                .contentShape(shape)
                .scaleEffect(pressed ? 0.92 : 1)
                .opacity(self.isEnabled ? 1 : 0.4)
                .animation(.easeOut(duration: 0.12), value: pressed)
                .animation(.easeOut(duration: 0.12), value: self.hovered)
                .onHover { self.hovered = $0 && self.isEnabled }
        }

        private var foreground: Color {
            if self.style.isOn {
                return Color(nsColor: .controlAccentColor)
            }
            if self.style.destructive, self.hovered {
                return Color(nsColor: .systemRed)
            }
            if let tint = self.style.tint {
                return tint
            }
            return self.hovered ? Color(nsColor: .labelColor) : Color(nsColor: .secondaryLabelColor)
        }

        private func fill(pressed: Bool) -> Color {
            if self.style.isOn {
                return InteractiveFill.accent(hovered: self.hovered, pressed: pressed)
            }
            if self.style.destructive, self.hovered || pressed {
                return Color(nsColor: .systemRed).opacity(pressed ? 0.24 : 0.14)
            }
            return InteractiveFill.neutral(
                hovered: self.hovered,
                pressed: pressed,
                resting: self.style.showsRestingFill ? 0.05 : 0)
        }
    }
}

/// Full-width list row: transparent at rest, highlighted on hover, darker while pressed.
struct RowButtonStyle: ButtonStyle {
    var cornerRadius: CGFloat = T.cornerRadius
    /// Hover state managed externally (e.g. by a popover anchor). Falls back to internal tracking when nil.
    var hovered: Bool?

    func makeBody(configuration: Configuration) -> some View {
        RowButtonBody(configuration: configuration, style: self)
    }

    private struct RowButtonBody: View {
        let configuration: ButtonStyleConfiguration
        let style: RowButtonStyle

        @Environment(\.isEnabled) private var isEnabled
        @State private var localHovered = false

        var body: some View {
            let hovered = (self.style.hovered ?? self.localHovered) && self.isEnabled
            self.configuration.label
                .background(
                    RoundedRectangle(cornerRadius: self.style.cornerRadius, style: .continuous)
                        .fill(InteractiveFill.neutral(hovered: hovered, pressed: self.configuration.isPressed)))
                .contentShape(RoundedRectangle(cornerRadius: self.style.cornerRadius, style: .continuous))
                .opacity(self.isEnabled ? 1 : 0.5)
                .animation(.easeOut(duration: 0.10), value: hovered)
                .onHover { self.localHovered = $0 }
        }
    }
}

/// Capsule chip used for compact actions (copy, filters). Bordered at rest so it reads as a button.
struct ChipButtonStyle: ButtonStyle {
    var selected: Bool = false
    var tint: Color?

    func makeBody(configuration: Configuration) -> some View {
        ChipButtonBody(configuration: configuration, style: self)
    }

    private struct ChipButtonBody: View {
        let configuration: ButtonStyleConfiguration
        let style: ChipButtonStyle

        @Environment(\.isEnabled) private var isEnabled
        @State private var hovered = false

        var body: some View {
            let pressed = self.configuration.isPressed
            let accent = self.style.tint ?? Color(nsColor: .controlAccentColor)
            let shape = Capsule(style: .continuous)

            self.configuration.label
                .font(.app(size: T.FontSize.caption, weight: self.style.selected ? .semibold : .medium))
                .foregroundStyle(self.style.selected ? Color.white : Color(nsColor: .labelColor).opacity(0.85))
                .lineLimit(1)
                .padding(.horizontal, T.space8)
                .frame(height: 20)
                .background {
                    if self.style.selected {
                        shape.fill(accent.opacity(pressed ? 0.75 : 1))
                    } else {
                        shape
                            .fill(InteractiveFill.neutral(hovered: self.hovered, pressed: pressed, resting: 0.05))
                            .overlay(shape.strokeBorder(Color.primary.opacity(0.10), lineWidth: T.stroke))
                    }
                }
                .contentShape(shape)
                .scaleEffect(pressed ? 0.96 : 1)
                .opacity(self.isEnabled ? 1 : 0.4)
                .animation(.easeOut(duration: 0.12), value: pressed)
                .animation(.easeOut(duration: 0.12), value: self.hovered)
                .onHover { self.hovered = $0 && self.isEnabled }
        }
    }
}

/// Small segment inside a tinted track (e.g. TUN stack picker); selected segment is raised.
struct MiniSegmentButtonStyle: ButtonStyle {
    var selected: Bool

    func makeBody(configuration: Configuration) -> some View {
        MiniSegmentBody(configuration: configuration, selected: self.selected)
    }

    private struct MiniSegmentBody: View {
        let configuration: ButtonStyleConfiguration
        let selected: Bool

        @Environment(\.isEnabled) private var isEnabled
        @Environment(\.colorScheme) private var colorScheme
        @State private var hovered = false

        var body: some View {
            let shape = RoundedRectangle(cornerRadius: 5, style: .continuous)
            self.configuration.label
                .font(.app(size: T.FontSize.caption, weight: self.selected ? .semibold : .medium))
                .foregroundStyle(self.selected ? Color(nsColor: .labelColor) : Color(nsColor: .secondaryLabelColor))
                .lineLimit(1)
                .padding(.horizontal, 7)
                .frame(height: 20)
                .background {
                    if self.selected {
                        shape
                            .fill(self.colorScheme == .dark
                                ? Color.white.opacity(0.22)
                                : Color(nsColor: .controlBackgroundColor))
                            .shadow(color: .black.opacity(0.18), radius: 1, y: 0.5)
                    } else {
                        shape.fill(InteractiveFill.neutral(
                            hovered: self.hovered,
                            pressed: self.configuration.isPressed))
                    }
                }
                .contentShape(shape)
                .opacity(self.isEnabled ? 1 : 0.4)
                .animation(.easeOut(duration: 0.12), value: self.hovered)
                .animation(.easeOut(duration: 0.12), value: self.configuration.isPressed)
                .onHover { self.hovered = $0 && self.isEnabled }
        }
    }
}

/// Minimal feedback for buttons whose label draws its own surface: dims while pressed.
struct PressableButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .opacity(configuration.isPressed ? 0.6 : 1)
            .animation(.easeOut(duration: 0.08), value: configuration.isPressed)
    }
}

/// Grouped inset surface, like a System Settings section.
struct GroupedSectionBackground: View {
    var cornerRadius: CGFloat = T.groupCornerRadius

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: self.cornerRadius, style: .continuous)
        shape
            .fill(Color.primary.opacity(0.045))
            .overlay(shape.strokeBorder(Color.primary.opacity(0.06), lineWidth: T.stroke))
    }
}

extension View {
    func groupedSection() -> some View {
        self
            .padding(T.space4)
            .background(GroupedSectionBackground())
    }
}

/// Pop-up menu drawn as a capsule chip (`value ⌃⌄`). Native `.bordered` menus render as an opaque
/// grey box with a blue indicator on glass, which clashes with the rest of the panel.
struct ChipMenu<Content: View>: View {
    let title: String
    var symbol: String?
    /// Caps titles that come from user data (e.g. policy group names); fixed option labels leave it nil.
    var maxTitleWidth: CGFloat?
    @ViewBuilder let content: () -> Content

    @Environment(\.isEnabled) private var isEnabled
    @State private var hovered = false

    var body: some View {
        Menu {
            self.content()
        } label: {
            HStack(spacing: T.space4) {
                if let symbol {
                    Image(systemName: symbol)
                        .foregroundStyle(Color(nsColor: .secondaryLabelColor))
                }
                Text(self.title)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .frame(maxWidth: self.maxTitleWidth)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 8, weight: .semibold))
                    .foregroundStyle(Color(nsColor: .secondaryLabelColor))
            }
            .font(.app(size: T.FontSize.caption, weight: .medium))
            .foregroundStyle(Color(nsColor: .labelColor))
            .padding(.horizontal, T.space8)
            .frame(height: 22)
            .background(
                Capsule(style: .continuous)
                    .fill(InteractiveFill.neutral(hovered: self.hovered, pressed: false, resting: 0.06))
                    .overlay(
                        Capsule(style: .continuous)
                            .strokeBorder(Color.primary.opacity(0.08), lineWidth: T.stroke)))
            .contentShape(Capsule(style: .continuous))
        }
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        // Never truncate the current value; rows decide what to drop when space runs out.
        .fixedSize()
        .opacity(self.isEnabled ? 1 : 0.4)
        .animation(.easeOut(duration: 0.12), value: self.hovered)
        .onHover { self.hovered = $0 && self.isEnabled }
    }
}

/// Self-drawn text field. The AppKit rounded-border field paints a light frame first and then
/// re-resolves its appearance on the glass panel, which flashes white in dark mode.
private struct AppTextFieldModifier: ViewModifier {
    @FocusState private var focused: Bool

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: 6, style: .continuous)
        content
            .textFieldStyle(.plain)
            .font(.app(size: T.FontSize.callout))
            .foregroundStyle(Color(nsColor: .labelColor))
            .focused(self.$focused)
            .padding(.horizontal, T.space8)
            .frame(height: 24)
            .background(shape.fill(Color.primary.opacity(0.06)))
            .overlay(
                shape.strokeBorder(
                    self.focused ? Color(nsColor: .controlAccentColor) : Color.primary.opacity(0.10),
                    lineWidth: self.focused ? 1.5 : T.stroke))
            .animation(.easeOut(duration: 0.12), value: self.focused)
    }
}

extension View {
    func appTextField() -> some View {
        self.modifier(AppTextFieldModifier())
    }
}
