import SwiftUI

// swiftlint:disable:next type_name
private typealias T = MenuBarLayoutTokens

extension MenuBarRootView {
    var modeAndTabSection: some View {
        VStack(spacing: T.space6) {
            self.modeSwitcher
            self.topTabs
        }
        .padding(.bottom, T.space4)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    // MARK: - Mode switcher (segmented: neutral track, raised selected segment)

    // Neutral on purpose: per Apple's HIG only one control should carry the accent on its
    // background, and color must not mean different things across the panel.
    var modeSwitcher: some View {
        HStack(spacing: 3) {
            self.modeButton(tr("ui.mode.rule"), mode: .rule)
            self.modeButton(tr("ui.mode.global"), mode: .global)
            self.modeButton(tr("ui.mode.direct"), mode: .direct)
        }
        .padding(3)
        .background(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(NeutralFill.track))
        .frame(width: contentWidth)
        .disabled(!self.appViewModel.isModeSwitchEnabled)
    }

    func modeButton(_ title: String, mode: CoreMode) -> some View {
        let selected = (self.switchingMode ?? self.appViewModel.currentMode) == mode

        return Button {
            // Taps during an in-flight switch are dropped here instead of disabling the buttons,
            // which would grey them all for a moment and read as flicker.
            guard self.appViewModel.isModeSwitchEnabled, self.switchingMode == nil,
                  mode != self.appViewModel.currentMode else { return }

            self.switchingMode = mode
            Task { @MainActor in
                await self.appViewModel.switchMode(to: mode)
                self.switchingMode = nil
            }
        } label: {
            Text(title)
                .font(.app(size: T.FontSize.body, weight: selected ? .semibold : .medium))
                .lineLimit(1)
                .frame(maxWidth: .infinity)
        }
        .buttonStyle(ModeSegmentButtonStyle(selected: selected))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    // MARK: - Tabs (neutral pill marks the current page)

    var topTabs: some View {
        HStack(spacing: T.space2) {
            ForEach(RootTab.allCases, id: \.self) { tab in
                self.tabSegmentButton(tab)
            }
        }
        .frame(width: contentWidth)
    }

    func tabSegmentButton(_ tab: RootTab) -> some View {
        let selected = self.rootViewModel.currentTab == tab

        return Button {
            // Swap pages instantly: a cross-fade over the translucent glass shows both pages blended.
            self.setCurrentTabWithoutAnimation(tab)
        } label: {
            Text(self.tr(tab.titleKey))
                .font(.app(size: T.FontSize.body, weight: selected ? .semibold : .regular))
                .lineLimit(1)
                .minimumScaleFactor(T.minimumScale)
                .foregroundStyle(selected ? nativePrimaryLabel : nativeSecondaryLabel)
                .frame(maxWidth: .infinity)
                .frame(height: 26)
        }
        .buttonStyle(TabPillButtonStyle(
            selected: selected,
            selectionNamespace: self.segmentedSelectionNamespace))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

private struct TabPillButtonStyle: ButtonStyle {
    let selected: Bool
    let selectionNamespace: Namespace.ID

    func makeBody(configuration: Configuration) -> some View {
        TabPillBody(configuration: configuration, style: self)
    }

    private struct TabPillBody: View {
        let configuration: ButtonStyleConfiguration
        let style: TabPillButtonStyle

        @State private var hovered = false

        var body: some View {
            let shape = Capsule(style: .continuous)
            let pressed = self.configuration.isPressed

            self.configuration.label
                .background {
                    if self.style.selected {
                        shape
                            .fill(InteractiveFill.selected(pressed: pressed))
                            .matchedGeometryEffect(id: "tab-selection", in: self.style.selectionNamespace)
                    } else {
                        shape.fill(InteractiveFill.neutral(hovered: self.hovered, pressed: pressed))
                    }
                }
                .contentShape(shape)
                .scaleEffect(pressed && !self.style.selected ? 0.97 : 1)
                .animation(.easeOut(duration: 0.12), value: pressed)
                .animation(.easeOut(duration: 0.12), value: self.hovered)
                .onHover { self.hovered = $0 }
        }
    }
}

/// One segment of the mode switcher. The selected segment is raised like a native segmented
/// control; unselected ones only show hover and press feedback.
private struct ModeSegmentButtonStyle: ButtonStyle {
    let selected: Bool

    func makeBody(configuration: Configuration) -> some View {
        ModeSegmentBody(configuration: configuration, selected: self.selected)
    }

    private struct ModeSegmentBody: View {
        let configuration: ButtonStyleConfiguration
        let selected: Bool

        @Environment(\.isEnabled) private var isEnabled
        @Environment(\.colorScheme) private var colorScheme
        @State private var hovered = false

        var body: some View {
            let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
            let pressed = self.configuration.isPressed

            self.configuration.label
                .foregroundStyle(self.selected ? Color(nsColor: .labelColor) : Color(nsColor: .secondaryLabelColor))
                .frame(height: 28)
                .background {
                    if self.selected {
                        shape
                            .fill(InteractiveFill.raisedSegment(self.colorScheme))
                            .overlay(shape.strokeBorder(NeutralFill.contrastBorder, lineWidth: T.stroke))
                            .shadow(color: .black.opacity(0.18), radius: 1, y: 0.5)
                    } else {
                        shape.fill(InteractiveFill.neutral(hovered: self.hovered, pressed: pressed))
                    }
                }
                .contentShape(shape)
                .opacity(self.isEnabled ? 1 : 0.5)
                .animation(.easeOut(duration: 0.12), value: self.hovered)
                .animation(.easeOut(duration: 0.12), value: pressed)
                .onHover { self.hovered = $0 && self.isEnabled }
        }
    }
}
