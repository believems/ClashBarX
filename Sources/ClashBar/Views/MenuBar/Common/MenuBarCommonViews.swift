import SwiftUI

// swiftlint:disable:next type_name
private typealias T = MenuBarLayoutTokens

struct SeparatedForEach<Element: Equatable, ID: Hashable, RowContent: View>: View {
    private struct Item: Identifiable {
        let id: ID
        let element: Element
        let isLast: Bool
    }

    private let items: [Item]
    private let separator: Color
    private let content: (Element) -> RowContent

    init<Data: RandomAccessCollection>(
        data: Data,
        id idPath: KeyPath<Element, ID>,
        separator: Color,
        @ViewBuilder content: @escaping (Element) -> RowContent) where Data.Element == Element
    {
        let array = Array(data)
        self.items = array.enumerated().map { index, el in
            Item(id: el[keyPath: idPath], element: el, isLast: index == array.count - 1)
        }
        self.separator = separator
        self.content = content
    }

    var body: some View {
        ForEach(self.items) { item in
            self.content(item.element)
            if !item.isLast {
                Rectangle()
                    .fill(self.separator)
                    .frame(height: T.stroke)
            }
        }
    }
}

struct CompactSelectionMenuConfiguration<Option: Hashable & Identifiable> {
    let selection: Option
    let options: [Option]
    let symbol: String
    let helpText: String
    let optionTitle: (Option) -> String
    let onSelect: (Option) -> Void
}

extension MenuBarRootView {
    /// Status-bar style footer: a hairline and one quiet row, no boxed surface. The old boxed
    /// background used window-background colors resolved through NSApp's appearance, which paint
    /// light for a frame when the popover opens in dark mode.
    var footerBar: some View {
        let mihomoRepositoryURL = URL(string: "https://github.com/MetaCubeX/mihomo")

        return VStack(spacing: 0) {
            Rectangle()
                .fill(NeutralFill.hairline)
                .frame(height: T.stroke)

            HStack(spacing: T.space2) {
                self.footerInfo(
                    tr("ui.footer.core_mihomo", self.footerMihomoVersionText),
                    url: mihomoRepositoryURL,
                    iconSystemName: "cpu")
                    .layoutPriority(1)

                self.footerCoreUpgradeControl

                Spacer(minLength: T.space6)

                self.footerVersionInfo
                    .fixedSize(horizontal: true, vertical: false)
            }
            .frame(height: 30)
        }
    }

    var footerMihomoVersionText: String {
        let version = self.appViewModel.version.trimmingCharacters(in: .whitespacesAndNewlines)
        guard version.first?.isNumber == true else { return version }
        return "v\(version)"
    }

    @ViewBuilder
    func footerInfo(_ text: String, url: URL?, iconSystemName: String? = nil) -> some View {
        if let url {
            Link(destination: url) {
                self.footerInfoLabel(text, iconSystemName: iconSystemName)
            }
            .buttonStyle(RowButtonStyle(cornerRadius: 6))
        } else {
            self.footerInfoLabel(text, iconSystemName: iconSystemName)
        }
    }

    func footerInfoLabel(_ text: String, iconSystemName: String?) -> some View {
        HStack(spacing: T.space4) {
            if let iconSystemName {
                Image(systemName: iconSystemName)
                    .font(.app(size: T.FontSize.caption, weight: .semibold))
                    .foregroundStyle(self.nativeSecondaryLabel)
            }

            Text(text)
                .font(.app(size: T.FontSize.caption, weight: .medium))
                .foregroundStyle(self.nativeSecondaryLabel)
                .lineLimit(1)
                .truncationMode(.middle)
                .minimumScaleFactor(T.minimumScale)
                .allowsTightening(true)
        }
        .padding(.horizontal, T.space6)
        .frame(height: 22)
        .help(text)
    }

    var footerCoreUpgradeControl: some View {
        self.compactAsyncIconButton(
            symbol: self.footerCoreUpgradeButtonSymbolName ?? "arrow.down.circle",
            label: self.footerCoreUpgradeButtonTitle,
            tint: self.footerCoreUpgradeButtonTint,
            isLoading: self.appViewModel.isCoreUpgradeInFlight,
            size: 20,
            showsRestingFill: false)
        {
            await self.appViewModel.upgradeCore()
        }
        .disabled(!self.isFooterCoreUpgradeEnabled)
        .help(self.footerCoreUpgradeButtonHelp)
    }

    var isFooterCoreUpgradeEnabled: Bool {
        self.appViewModel.isRuntimeRunning && !self.appViewModel.isCoreUpgradeInFlight
    }

    var footerCoreUpgradeButtonTitle: String {
        switch self.appViewModel.coreUpgradeState {
        case .idle:
            tr("ui.action.upgrade_core")
        case .running:
            tr("ui.footer.core_upgrade.running")
        case .succeeded:
            tr("ui.footer.core_upgrade.success")
        case .alreadyLatest:
            tr("ui.footer.core_upgrade.latest")
        case .failed:
            tr("ui.footer.core_upgrade.failed")
        }
    }

    var footerCoreUpgradeButtonSymbolName: String? {
        switch self.appViewModel.coreUpgradeState {
        case .idle:
            "arrow.down.circle"
        case .running:
            nil
        case .succeeded:
            "checkmark.circle.fill"
        case .alreadyLatest:
            "checkmark.circle"
        case .failed:
            "exclamationmark.triangle.fill"
        }
    }

    var footerCoreUpgradeButtonTint: Color? {
        switch self.appViewModel.coreUpgradeState {
        case .idle, .running:
            nil
        case .succeeded, .alreadyLatest:
            self.nativePositive.opacity(T.Opacity.solid)
        case .failed:
            self.nativeCritical.opacity(T.Opacity.solid)
        }
    }

    var footerCoreUpgradeButtonHelp: String {
        if !self.appViewModel.isRuntimeRunning {
            return tr("ui.footer.core_upgrade.help.disabled")
        }

        switch self.appViewModel.coreUpgradeState {
        case .idle:
            return tr("ui.footer.core_upgrade.help")
        case .running:
            return tr("ui.footer.core_upgrade.help.running")
        case .succeeded:
            return tr("ui.footer.core_upgrade.help.success")
        case let .alreadyLatest(version):
            if let version, !version.isEmpty {
                return tr("ui.footer.core_upgrade.help.latest_version", version)
            }
            return tr("ui.footer.core_upgrade.help.latest")
        case let .failed(message):
            return tr("ui.footer.core_upgrade.help.failed", message)
        }
    }

    @ViewBuilder
    var footerVersionInfo: some View {
        let currentText = tr("ui.footer.version", self.appViewModel.currentAppVersionText)
        if let update = self.appViewModel.availableAppUpdate {
            Link(destination: update.releaseURL) {
                Label(tr("ui.footer.version", update.displayVersion), systemImage: "arrow.down.circle.fill")
                    .labelStyle(.titleAndIcon)
            }
            .buttonStyle(ChipButtonStyle(selected: true, tint: self.nativeAccent))
            .help(tr("ui.footer.version_update_help", update.displayVersion))
            .accessibilityLabel(tr(
                "ui.footer.version_update_accessibility",
                self.appViewModel.currentAppVersionText,
                update.displayVersion))
        } else if let releaseIndexURL = self.appViewModel.appReleaseIndexURL {
            Link(destination: releaseIndexURL) {
                self.footerVersionLabel(currentText)
            }
            .buttonStyle(RowButtonStyle(cornerRadius: 6))
            .help(currentText)
        } else {
            self.footerVersionLabel(currentText)
        }
    }

    func footerVersionLabel(_ text: String) -> some View {
        Text(text)
            .font(.app(size: T.FontSize.caption, weight: .medium))
            .monospacedDigit()
            .foregroundStyle(self.nativeSecondaryLabel)
            .lineLimit(1)
            .padding(.horizontal, T.space6)
            .frame(height: 22)
    }
}

extension TranslatingView {
    var statusColor: Color {
        switch appViewModel.runtimeVisualStatus {
        case .runningHealthy: self.nativePositive.opacity(T.Opacity.solid)
        case .runningDegraded: self.nativeWarning.opacity(T.Opacity.solid)
        case .starting: self.nativeWarning.opacity(T.Opacity.solid)
        case .failed: self.nativeCritical.opacity(T.Opacity.solid)
        case .stopped: self.nativeSecondaryLabel
        }
    }
}

extension View {
    var nativeAccent: Color {
        Color(nsColor: .controlAccentColor)
    }

    var nativePositive: Color {
        self.isDarkAppearance
            ? Color(nsColor: .systemGreen)
            : Color(red: 0.06, green: 0.32, blue: 0.16)
    }

    var nativeWarning: Color {
        Color(nsColor: .systemOrange)
    }

    var nativeCritical: Color {
        Color(nsColor: .systemRed)
    }

    var nativeInfo: Color {
        Color(nsColor: .systemBlue)
    }

    var nativeTeal: Color {
        Color(nsColor: .systemTeal)
    }

    var nativeIndigo: Color {
        Color(nsColor: .systemIndigo)
    }

    var nativePurple: Color {
        Color(nsColor: .systemPurple)
    }

    /// Traffic direction tints. Orange/teal differ in hue and lightness (also for color-blind users),
    /// and teal keeps download apart from the accent color, which marks interactive state only.
    /// Apply them to glyphs and chart lines only and keep numbers in label colors for legibility.
    var trafficUploadTint: Color {
        Color(nsColor: .systemOrange)
    }

    var trafficDownloadTint: Color {
        Color(nsColor: .systemTeal)
    }

    var nativePrimaryLabel: Color {
        Color(nsColor: .labelColor)
    }

    var nativeBadgeFill: Color {
        Color(nsColor: .quaternaryLabelColor).opacity(T.Opacity.tint)
    }

    var nativeSelectedMenuText: Color {
        Color(nsColor: .selectedMenuItemTextColor)
    }

    var nativeShadow: Color {
        Color(nsColor: .shadowColor)
    }

    var nativeSecondaryLabel: Color {
        Color(nsColor: .secondaryLabelColor)
    }

    var nativeTertiaryLabel: Color {
        Color(nsColor: .tertiaryLabelColor)
    }

    var nativeSeparator: Color {
        Color(nsColor: .separatorColor)
    }

    var nativeControlFill: Color {
        Color(nsColor: .windowBackgroundColor)
    }

    var nativeControlBorder: Color {
        Color(nsColor: .separatorColor)
    }

    /// Highlight of the hovered item in a menu-style list; pair it with `nativeSelectedMenuText`.
    /// Panel rows use neutral hover fills instead, since this is the system selection color.
    var nativeMenuHighlightFill: Color {
        Color(nsColor: .selectedContentBackgroundColor)
    }

    func nativeHoverRowBackground(
        _ hovered: Bool,
        cornerRadius: CGFloat = T.cornerRadius) -> some View
    {
        RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
            .fill(InteractiveFill.neutral(hovered: hovered, pressed: false))
    }

    func nativeBadgeCapsule() -> some View {
        Capsule(style: .continuous).fill(self.nativeBadgeFill)
    }

    func filterChip(
        title: String,
        count: Int? = nil,
        selected: Bool,
        action: @escaping () -> Void) -> some View
    {
        Button(action: action) {
            HStack(spacing: T.space4) {
                Text(title)
                if let count {
                    Text("\(count)")
                        .monospacedDigit()
                        .opacity(0.7)
                }
            }
            .fixedSize()
        }
        .buttonStyle(ChipButtonStyle(selected: selected))
    }

    func nativeRowFill(active: Bool, hovered: Bool) -> Color {
        if active {
            return self.nativeAccent.opacity(T.Opacity.selection)
        }
        if hovered {
            return InteractiveFill.neutral(hovered: true, pressed: false)
        }
        return self.nativeControlFill
    }

    func nativeActionBackground(hovered: Bool, destructive: Bool = false) -> Color {
        guard hovered else { return .clear }
        return destructive
            ? self.nativeCritical.opacity(T.Opacity.tint)
            : InteractiveFill.neutral(hovered: true, pressed: false)
    }

    func nativeActionForeground(hovered: Bool, destructive: Bool = false) -> Color {
        if destructive {
            return hovered ? self.nativeCritical : self.nativeSecondaryLabel
        }
        return hovered ? self.nativePrimaryLabel : self.nativeSecondaryLabel
    }

    func tabControlCard(@ViewBuilder _ content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: T.space4) {
            content()
        }
        .menuRowPadding(vertical: T.space4)
    }

    func filterTextField(_ placeholder: String, text: Binding<String>) -> some View {
        FilterTextField(placeholder: placeholder, text: text)
    }

    func emptyCard(_ text: String) -> some View {
        Text(text)
            .font(.app(size: T.FontSize.body, weight: .regular))
            .foregroundStyle(self.nativeSecondaryLabel)
            .frame(maxWidth: .infinity, alignment: .leading)
            .menuRowPadding()
    }

    func fractionSummaryBadge(current: Int, total: Int) -> some View {
        HStack(spacing: T.space1) {
            Text("\(current)")
                .font(.app(size: T.FontSize.caption, weight: .bold))
                .foregroundStyle(self.nativePrimaryLabel)
            Text("/")
                .font(.app(size: T.FontSize.caption, weight: .medium))
                .foregroundStyle(self.nativeTertiaryLabel)
            Text("\(total)")
                .font(.app(size: T.FontSize.caption, weight: .medium))
                .foregroundStyle(self.nativeSecondaryLabel)
        }
        .padding(.horizontal, T.space6)
        .padding(.vertical, T.space2)
        .background(self.nativeBadgeCapsule())
    }

    func compactAsyncIconButton(
        symbol: String,
        label: String,
        tint: Color? = nil,
        role: ButtonRole? = nil,
        isOn: Bool = false,
        destructive: Bool = false,
        isLoading: Bool = false,
        size: CGFloat = 24,
        showsRestingFill: Bool = true,
        action: @escaping () async -> Void) -> some View
    {
        CompactAsyncIconButton(
            symbol: symbol,
            style: IconButtonStyle(
                size: size,
                isOn: isOn,
                tint: tint,
                destructive: destructive,
                showsRestingFill: showsRestingFill),
            role: role,
            isLoading: isLoading,
            action: action)
            .accessibilityLabel(label)
            .help(label)
    }

    /// Toolbar-style icon button. `isOn` shows a toggled state; `warning` turns red on hover.
    func compactTopIcon(
        _ symbol: String,
        label: String,
        role: ButtonRole? = nil,
        warning: Bool = false,
        isOn: Bool = false,
        tint: Color? = nil,
        isLoading: Bool = false,
        size: CGFloat = 24,
        action: @escaping () async -> Void) -> some View
    {
        self.compactAsyncIconButton(
            symbol: symbol,
            label: label,
            tint: tint,
            role: role,
            isOn: isOn,
            destructive: warning,
            isLoading: isLoading,
            size: size,
            action: action)
    }

    func compactSelectionMenu(
        _ configuration: CompactSelectionMenuConfiguration<some Hashable & Identifiable>,
        showsSymbol: Bool = true,
        maxTitleWidth: CGFloat? = nil) -> some View
    {
        ChipMenu(
            title: configuration.optionTitle(configuration.selection),
            symbol: showsSymbol ? configuration.symbol : nil,
            maxTitleWidth: maxTitleWidth)
        {
            ForEach(configuration.options) { option in
                Button {
                    configuration.onSelect(option)
                } label: {
                    if configuration.selection == option {
                        Label(configuration.optionTitle(option), systemImage: "checkmark")
                    } else {
                        Text(configuration.optionTitle(option))
                    }
                }
            }
        }
        .help(configuration.helpText)
    }

    func compareLatency(lhs: Int?, rhs: Int?, ascending: Bool) -> ComparisonResult {
        let leftAvailable = self.isProxyNodeAvailable(lhs)
        let rightAvailable = self.isProxyNodeAvailable(rhs)
        if leftAvailable != rightAvailable {
            return leftAvailable ? .orderedAscending : .orderedDescending
        }
        guard leftAvailable, rightAvailable, let lhs, let rhs, lhs != rhs else {
            return .orderedSame
        }
        return (ascending ? lhs < rhs : lhs > rhs) ? .orderedAscending : .orderedDescending
    }

    func isProxyNodeAvailable(_ latency: Int?) -> Bool {
        guard let latency else { return false }
        return latency > 0
    }

    func latencyColor(_ value: Int?) -> Color {
        guard value != nil else { return self.nativeTertiaryLabel }
        switch LatencyLevel(delay: value) {
        case .excellent:
            return self.nativePositive.opacity(T.Opacity.solid)
        case .good:
            return self.nativeTeal.opacity(T.Opacity.solid)
        case .fair:
            return self.nativeWarning.opacity(T.Opacity.solid)
        case .poor, .timeout:
            return self.nativeCritical.opacity(T.Opacity.solid)
        }
    }

    func nextHovered<V: Equatable>(current: V?, target: V, isHovering: Bool) -> V? {
        isHovering ? target : (current == target ? nil : current)
    }

    func machineStatusTint(_ status: MachineConnectionStatus) -> Color {
        switch status {
        case .unknown:
            self.nativeSecondaryLabel
        case .checking:
            self.nativeWarning.opacity(T.Opacity.solid)
        case .connected:
            self.nativePositive.opacity(T.Opacity.solid)
        case .failed:
            self.nativeCritical.opacity(T.Opacity.solid)
        }
    }
}

struct CompactAsyncIconButton: View {
    let symbol: String
    let style: IconButtonStyle
    let role: ButtonRole?
    let isLoading: Bool
    let action: () async -> Void

    var body: some View {
        Button(role: self.role) {
            Task { await self.action() }
        } label: {
            ZStack {
                Image(systemName: self.symbol)
                    .opacity(self.isLoading ? 0 : 1)

                ProgressView()
                    .controlSize(.mini)
                    .opacity(self.isLoading ? 1 : 0)
            }
        }
        .buttonStyle(self.style)
        .allowsHitTesting(!self.isLoading)
    }
}

private struct FilterTextField: View {
    let placeholder: String
    @Binding var text: String

    @State private var hovered = false

    var body: some View {
        TextField(self.placeholder, text: self.$text)
            .textFieldStyle(.roundedBorder)
            .font(.app(size: T.FontSize.body, weight: .regular))
            .foregroundStyle(self.nativePrimaryLabel)
            .overlay(alignment: .trailing) {
                if !self.text.isEmpty {
                    Button {
                        self.text = ""
                        self.hovered = false
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .font(.app(size: T.FontSize.subheadline, weight: .medium))
                            .foregroundStyle(self.hovered ? self.nativeSecondaryLabel : self.nativeTertiaryLabel)
                            .frame(width: T.compactRowHeight, height: T.compactRowHeight)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.borderless)
                    .padding(.trailing, T.space2)
                    .onHover { self.hovered = $0 }
                    .onDisappear { self.hovered = false }
                }
            }
    }
}

extension RuntimeVisualStatus {
    var color: Color {
        switch self {
        case .runningHealthy: Color(nsColor: .systemGreen).opacity(T.Opacity.solid)
        case .runningDegraded: Color(nsColor: .systemOrange).opacity(T.Opacity.solid)
        case .starting: Color(nsColor: .systemBlue).opacity(T.Opacity.solid)
        case .failed: Color(nsColor: .systemRed).opacity(T.Opacity.solid)
        case .stopped: Color(nsColor: .secondaryLabelColor)
        }
    }
}

extension View {
    var isDarkAppearance: Bool {
        NSApp.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
    }
}
