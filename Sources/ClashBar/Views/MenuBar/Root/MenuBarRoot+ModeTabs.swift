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

    // MARK: - Mode switcher (native segmented control)

    var modeSwitcher: some View {
        let modes: [CoreMode] = [.rule, .global, .direct]
        let shown = self.switchingMode ?? self.appViewModel.currentMode

        return FillSegmentedControl(
            labels: [tr("ui.mode.rule"), tr("ui.mode.global"), tr("ui.mode.direct")],
            selectedIndex: modes.firstIndex(of: shown))
        { index in
            let mode = modes[index]
            // Taps during an in-flight switch are dropped here. Disabling the control for that
            // window would grey every segment for a moment, which reads as the labels flickering.
            guard self.appViewModel.isModeSwitchEnabled, self.switchingMode == nil,
                  mode != self.appViewModel.currentMode else { return }

            self.switchingMode = mode
            Task { @MainActor in
                await self.appViewModel.switchMode(to: mode)
                self.switchingMode = nil
            }
        }
        .frame(width: contentWidth)
        .disabled(!self.appViewModel.isModeSwitchEnabled)
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
