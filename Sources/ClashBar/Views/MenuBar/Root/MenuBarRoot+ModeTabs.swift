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

    // MARK: - Mode switcher (native-style segmented control with a raised selection)

    var modeSwitcher: some View {
        HStack(spacing: T.space2) {
            self.modeSegmentButton(
                title: tr("ui.mode.rule"),
                mode: .rule,
                symbol: "arrow.triangle.branch")
            self.modeSegmentButton(
                title: tr("ui.mode.global"),
                mode: .global,
                symbol: "globe")
            self.modeSegmentButton(
                title: tr("ui.mode.direct"),
                mode: .direct,
                symbol: "arrow.right")
        }
        .padding(T.space2)
        .frame(width: contentWidth)
        .background(
            RoundedRectangle(cornerRadius: 9, style: .continuous)
                .fill(Color.primary.opacity(self.isDarkAppearance ? 0.08 : 0.06)))
        .opacity(appViewModel.isModeSwitchEnabled ? 1 : 0.5)
    }

    func modeSegmentButton(title: String, mode: CoreMode, symbol: String) -> some View {
        let selected = appViewModel.currentMode == mode
        let switchingThisMode = switchingMode == mode

        return Button {
            guard appViewModel.isModeSwitchEnabled, switchingMode == nil,
                  mode != appViewModel.currentMode else { return }

            switchingMode = mode
            Task { @MainActor in
                await appViewModel.switchMode(to: mode)
                switchingMode = nil
            }
        } label: {
            HStack(spacing: T.space4) {
                if switchingThisMode {
                    ProgressView()
                        .controlSize(.mini)
                        .frame(width: 12, height: 12)
                } else {
                    Image(systemName: symbol)
                        .font(.system(size: T.FontSize.callout, weight: .semibold))
                        .foregroundStyle(selected ? nativeAccent : nativeSecondaryLabel)
                }

                Text(title)
                    .font(.app(size: T.FontSize.callout, weight: selected ? .semibold : .medium))
                    .lineLimit(1)
                    .foregroundStyle(selected ? nativePrimaryLabel : nativeSecondaryLabel)
            }
            .frame(maxWidth: .infinity)
            .frame(height: 26)
        }
        .buttonStyle(SegmentButtonStyle(
            selected: selected,
            selectionNamespace: self.segmentedSelectionNamespace,
            selectionID: "mode-selection"))
        .accessibilityAddTraits(selected ? .isSelected : [])
        .animation(.snappy(duration: 0.2), value: appViewModel.currentMode)
    }

    // MARK: - Tabs (accent-tinted pill marks the current page)

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
                .foregroundStyle(selected ? nativeAccent : nativeSecondaryLabel)
                .frame(maxWidth: .infinity)
                .frame(height: 26)
        }
        .buttonStyle(TabPillButtonStyle(
            selected: selected,
            selectionNamespace: self.segmentedSelectionNamespace))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

/// Segment inside a track: the selected segment is a raised "thumb" like NSSegmentedControl.
private struct SegmentButtonStyle: ButtonStyle {
    let selected: Bool
    let selectionNamespace: Namespace.ID
    let selectionID: String

    func makeBody(configuration: Configuration) -> some View {
        SegmentBody(configuration: configuration, style: self)
    }

    private struct SegmentBody: View {
        let configuration: ButtonStyleConfiguration
        let style: SegmentButtonStyle

        @Environment(\.colorScheme) private var colorScheme
        @State private var hovered = false

        var body: some View {
            let shape = RoundedRectangle(cornerRadius: 7, style: .continuous)
            let pressed = self.configuration.isPressed

            self.configuration.label
                .background {
                    if self.style.selected {
                        shape
                            .fill(self.colorScheme == .dark
                                ? Color.white.opacity(0.18)
                                : Color(nsColor: .controlBackgroundColor))
                            .shadow(color: .black.opacity(self.colorScheme == .dark ? 0.3 : 0.12), radius: 1.5, y: 0.5)
                            .matchedGeometryEffect(id: self.style.selectionID, in: self.style.selectionNamespace)
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
                            .fill(InteractiveFill.accent(hovered: false, pressed: pressed))
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
