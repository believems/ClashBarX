import SwiftUI

// swiftlint:disable:next type_name
private typealias T = MenuBarLayoutTokens

private struct SettingsSelectionRowConfiguration<Option: Hashable> {
    let title: String
    let symbol: String
    let valueText: String
    let options: [Option]
    let optionTitle: (Option) -> String
    let isSelected: (Option) -> Bool
    let onSelect: (Option) -> Void
}

struct SystemTabView: TranslatingView {
    /// 同一视图按 scope 渲染两个 tab：设置（应用/外观向）与网络（代理/核心向），
    /// 共享全部行组件，避免组件抽协议的成本。
    enum ContentScope {
        case appSettings
        case network
    }

    @EnvironmentObject var appViewModel: AppViewModel
    let scope: ContentScope
    @AppStorage("clashbar.proxy.traffic_overview.visible") var showTrafficOverview: Bool = true
    @State private var isExceptionsExpanded = false

    init(scope: ContentScope) {
        self.scope = scope
    }


    func settingsSection(_ title: String, @ViewBuilder content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: T.space2) {
            Text(title)
                .font(.app(size: T.FontSize.caption, weight: .semibold))
                .foregroundStyle(nativeSecondaryLabel)
                .padding(.leading, T.space8)
                .frame(minHeight: 22)

            VStack(spacing: 0) {
                content()
            }
            .groupedSection()
        }
    }

    /// 守护周期（秒）的字符串视图绑定：输入为 30–86400 的整数时回写。
    private var proxyGuardIntervalText: Binding<String> {
        Binding(
            get: { String(max(30, self.appViewModel.proxyGuardIntervalSeconds)) },
            set: { text in
                guard let value = Int(text.trimmingCharacters(in: .whitespaces)),
                      (30...86_400).contains(value) else { return }
                self.appViewModel.proxyGuardIntervalSeconds = value
            })
    }

    private var corpProxyPortText: Binding<String> {
        Binding(
            get: { String(self.appViewModel.corpProxyPort) },
            set: { text in
                guard let value = Int(text.trimmingCharacters(in: .whitespaces)),
                      (1...65_535).contains(value) else { return }
                self.appViewModel.corpProxyPort = value
            })
    }

    func settingsRowLabel(symbol: String, title: String) -> some View {
        HStack(spacing: T.space6) {
            Image(systemName: symbol)
                .font(.system(size: T.FontSize.callout, weight: .regular))
                .foregroundStyle(nativeSecondaryLabel)
                .frame(width: 16, alignment: .center)
            Text(title)
                .font(.app(size: T.FontSize.body, weight: .medium))
                .foregroundStyle(nativePrimaryLabel)
                .lineLimit(1)
                .truncationMode(.tail)
        }
    }

    func settingsToggleRow(
        _ title: String,
        symbol: String,
        isOn: Binding<Bool>,
        isDisabled: Bool = false) -> some View
    {
        HStack(spacing: T.space8) {
            self.settingsRowLabel(symbol: symbol, title: title)
                .layoutPriority(1)
            Spacer(minLength: 0)
            Toggle("", isOn: isOn)
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.small)
                .disabled(isDisabled)
        }
        .menuRowPadding(vertical: T.space4)
    }

    func settingsMenuRow(
        _ title: String,
        symbol: String,
        valueText: String,
        controlWidth: CGFloat? = nil,
        popoverWidth: CGFloat? = nil,
        @ViewBuilder options: @escaping (_ dismiss: @escaping () -> Void) -> some View) -> some View
    {
        let resolvedControlWidth = controlWidth ?? self.settingsMenuControlWidth

        return HStack(spacing: T.space8) {
            self.settingsRowLabel(symbol: symbol, title: title)
                .layoutPriority(1)
            Spacer(minLength: 0)
            AttachedPopoverMenu(width: popoverWidth ?? resolvedControlWidth) { _ in
                HStack(spacing: T.space2) {
                    Text(valueText)
                        .foregroundStyle(nativeSecondaryLabel)
                        .lineLimit(1)
                        .truncationMode(.tail)
                    Image(systemName: "chevron.right")
                        .font(.app(size: T.FontSize.caption, weight: .semibold))
                        .foregroundStyle(nativeTertiaryLabel)
                }
                .font(.app(size: T.FontSize.caption, weight: .medium))
                .frame(maxWidth: .infinity, alignment: .trailing)
            } content: { dismiss in
                options(dismiss)
            }
            .frame(width: resolvedControlWidth, alignment: .trailing)
            .appBorderedButtonStyle()
            .controlSize(.small)
        }
        .menuRowPadding(vertical: T.space4)
    }

    private func settingsSelectionRow(
        _ configuration: SettingsSelectionRowConfiguration<some Hashable>) -> some View
    {
        self.settingsMenuRow(
            configuration.title,
            symbol: configuration.symbol,
            valueText: configuration.valueText)
        { dismiss in
            ForEach(configuration.options, id: \.self) { option in
                AttachedPopoverMenuItem(
                    title: configuration.optionTitle(option),
                    selected: configuration.isSelected(option))
                {
                    configuration.onSelect(option)
                    dismiss()
                }
            }
        }
    }

    func settingsPortFieldRow(_ title: String, symbol: String, text: Binding<String>) -> some View {
        HStack(spacing: T.space8) {
            self.settingsRowLabel(symbol: symbol, title: title)
                .layoutPriority(1)

            Spacer(minLength: 0)

            TextField(self.tr("ui.placeholder.port"), text: text)
                .multilineTextAlignment(.trailing)
                .appTextField()
                .frame(width: self.settingsPortFieldWidth, alignment: .trailing)
                .onChange(of: text.wrappedValue) { _ in
                    self.appViewModel.scheduleProxyPortsAutoSaveIfNeeded()
                }
                .onSubmit {
                    Task { await self.appViewModel.applyProxyPorts(autoSaved: true) }
                }
        }
    }

    func systemProxyExceptionBinding(id: EditableSystemProxyException.ID) -> Binding<String> {
        Binding(
            get: {
                self.appViewModel.systemProxyExceptions.first(where: { $0.id == id })?.value ?? ""
            },
            set: { value in
                guard let index = self.appViewModel.systemProxyExceptions.firstIndex(where: { $0.id == id }) else {
                    return
                }
                self.appViewModel.systemProxyExceptions[index].value = value
            })
    }

    func systemProxyExceptionRow(_ exception: EditableSystemProxyException) -> some View {
        HStack(spacing: T.space4) {
            TextField(
                self.tr("ui.placeholder.system_proxy_exception"),
                text: self.systemProxyExceptionBinding(id: exception.id))
                .appTextField()

            Button {
                self.appViewModel.removeSystemProxyExceptionDraft(id: exception.id)
            } label: {
                Image(systemName: "minus.circle")
            }
            .buttonStyle(IconButtonStyle(size: 22, destructive: true, showsRestingFill: false))
            .accessibilityLabel(self.tr("ui.action.remove_exception"))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    func maintenanceActionButton(_ title: String, symbol: String, action: @escaping () async -> Void) -> some View {
        Button {
            Task { await action() }
        } label: {
            Label {
                Text(title)
                    .lineLimit(1)
                    .multilineTextAlignment(.center)
            } icon: {
                Image(systemName: symbol)
            }
            .frame(maxWidth: .infinity, alignment: .center)
        }
        .appBorderedButtonStyle()
        .controlSize(.small)
        .disabled(!self.maintenanceActionEnabled)
        .opacity(self.maintenanceActionEnabled ? 1 : 0.62)
    }

    func proxyGuardOutcomeRow(_ outcome: ProxyGuardProbeOutcome) -> some View {
        let isReachable = outcome == .reachable
        let symbol = isReachable ? "checkmark.circle" : "exclamationmark.triangle"
        let text = {
            switch outcome {
            case .reachable:
                self.tr("ui.settings.proxy_guard_probe_ok")
            case let .failed(consecutiveCount):
                self.tr("ui.settings.proxy_guard_probe_failed", consecutiveCount)
            }
        }()
        return HStack(spacing: T.space6) {
            Image(systemName: symbol)
                .font(.app(size: T.FontSize.caption, weight: .semibold))
                .foregroundStyle(isReachable ? nativePositive : nativeWarning)
            Text(text)
                .font(.app(size: T.FontSize.caption, weight: .regular))
                .foregroundStyle(nativeSecondaryLabel)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    func settingsFeedbackBanner(text: String, color: Color, symbol: String) -> some View {
        HStack(spacing: T.space6) {
            Image(systemName: symbol)
                .font(.app(size: T.FontSize.caption, weight: .semibold))
                .foregroundStyle(color)

            Text(text)
                .font(.app(size: T.FontSize.caption, weight: .medium))
                .foregroundStyle(nativePrimaryLabel)
                .lineLimit(2)

            Spacer(minLength: 0)
        }
        .menuRowPadding(vertical: T.space4)
        .background {
            RoundedRectangle(cornerRadius: T.cornerRadius, style: .continuous)
                .fill(nativeControlFill)
                .overlay {
                    RoundedRectangle(cornerRadius: T.cornerRadius, style: .continuous)
                        .stroke(color.opacity(0.26), lineWidth: T.stroke)
                }
                .shadow(
                    color: self.nativeShadow.opacity(T.Shadow.standard.opacity),
                    radius: T.Shadow.standard.radius,
                    x: T.Shadow.standard.x,
                    y: T.Shadow.standard.y)
        }
    }

    func statusBarModeLabel(_ mode: StatusBarDisplayMode) -> String {
        switch mode {
        case .iconAndSpeed:
            self.tr("ui.settings.display_mode.icon_and_speed")
        case .iconOnly:
            self.tr("ui.settings.display_mode.icon_only")
        case .speedOnly:
            self.tr("ui.settings.display_mode.speed_only")
        }
    }

    func appearanceModeLabel(_ mode: AppAppearanceMode) -> String {
        switch mode {
        case .system:
            self.tr("ui.settings.appearance.system")
        case .light:
            self.tr("ui.settings.appearance.light")
        case .dark:
            self.tr("ui.settings.appearance.dark")
        }
    }

    var settingsMenuControlWidth: CGFloat {
        let contentWidth = T.panelWidth - (T.space8 * 2)
        return min(152, max(118, contentWidth * 0.43))
    }

    var settingsPortFieldWidth: CGFloat {
        let contentWidth = T.panelWidth - (T.space8 * 2)
        return min(108, max(92, contentWidth * 0.30))
    }

    var maintenanceActionEnabled: Bool {
        SystemTabViewModel.maintenanceActionEnabled(session: self.appViewModel)
    }

    var geoUpdateActionEnabled: Bool {
        self.appViewModel.isRuntimeRunning && !self.appViewModel.isGeoUpdateInFlight
    }

    var geoUpdateFeedbackSymbol: String? {
        switch self.appViewModel.geoUpdateState {
        case .idle, .updating:
            nil
        case .succeeded:
            "checkmark.circle"
        case .failed:
            "exclamationmark.triangle"
        }
    }

    var geoUpdateFeedbackColor: Color {
        switch self.appViewModel.geoUpdateState {
        case .succeeded:
            nativePositive.opacity(T.Opacity.solid)
        case .failed:
            nativeCritical.opacity(T.Opacity.solid)
        default:
            nativeSecondaryLabel
        }
    }

    var isSystemProxyExceptionsSyncing: Bool {
        self.appViewModel.settingsSyncingKey == "system-proxy-exceptions"
    }

    var settingsFeedbackState: (message: String, color: Color, symbol: String)? {
        guard let feedback = SystemTabViewModel.feedbackState(session: appViewModel) else { return nil }
        let color: Color = switch feedback.kind {
        case .error:
            nativeCritical.opacity(T.Opacity.solid)
        case .warning:
            nativeWarning.opacity(T.Opacity.solid)
        case .success:
            nativePositive.opacity(T.Opacity.solid)
        }
        return (feedback.message, color, feedback.symbol)
    }

    func editableCoreSettingBinding(_ setting: AppViewModel.EditableCoreSetting) -> Binding<Bool> {
        Binding(
            get: { self.appViewModel.boolValue(for: setting) },
            set: { value in
                Task { await self.appViewModel.applyEditableCoreSetting(setting, to: value) }
            })
    }

    var body: some View {
        let isRemote = self.appViewModel.isRemoteTarget
        let proxyPortFields: [(titleKey: String, symbol: String, text: Binding<String>)] = [
            ("ui.settings.port.port", "network", $appViewModel.settingsPort),
            ("ui.settings.port.socks", "wave.3.right", self.$appViewModel.settingsSocksPort),
            ("ui.settings.port.mixed", "arrow.triangle.merge", self.$appViewModel.settingsMixedPort),
            ("ui.settings.port.redir", "arrowshape.turn.up.right", self.$appViewModel.settingsRedirPort),
            ("ui.settings.port.tproxy", "shield.lefthalf.filled", self.$appViewModel.settingsTProxyPort),
        ]
        let localOnlyItems: [(id: String, title: String, symbol: String, isOn: Binding<Bool>)] = [
            (
                "launch-at-login",
                tr("ui.settings.launch_at_login"),
                "person.crop.circle.badge.checkmark",
                Binding(
                    get: { self.appViewModel.launchAtLoginEnabled },
                    set: { self.appViewModel.applyLaunchAtLogin($0) })),
            (
                "auto-start-core",
                tr("ui.settings.auto_start_core"),
                "power.circle",
                Binding(
                    get: { self.appViewModel.autoStartCoreEnabled },
                    set: { self.appViewModel.autoStartCoreEnabled = $0 })),
        ]
        let coreToggleItems: [(id: String, title: String, symbol: String, isOn: Binding<Bool>)] = [
            (
                AppViewModel.EditableCoreSetting.allowLan.id,
                self.tr("ui.settings.allow_lan"),
                "network",
                self.editableCoreSettingBinding(.allowLan)),
            (
                AppViewModel.EditableCoreSetting.ipv6.id,
                self.tr("ui.settings.ipv6"),
                "globe",
                self.editableCoreSettingBinding(.ipv6)),
            (
                AppViewModel.EditableCoreSetting.tcpConcurrent.id,
                self.tr("ui.settings.tcp_concurrent"),
                "point.3.connected.trianglepath.dotted",
                self.editableCoreSettingBinding(.tcpConcurrent)),
        ]
        let maintenanceActions: [(titleKey: String, symbol: String, action: @MainActor () async -> Void)] = [
            (
                "ui.action.flush_fakeip_cache",
                "externaldrive.badge.minus",
                { await self.appViewModel.flushFakeIPCache() }),
            (
                "ui.action.flush_dns_cache",
                "network.badge.shield.half.filled",
                { await self.appViewModel.flushDNSCache() }),
        ]
        let selectedLogLevel = self.appViewModel.stringValue(for: .logLevel)
        let systemProxyExceptionsTitle = isRemote
            ? "\(tr("ui.section.system_proxy_exceptions")) (\(tr("ui.machine.local_label")))"
            : self.tr("ui.section.system_proxy_exceptions")

        return VStack(alignment: .leading, spacing: T.space6) {
            if self.scope == .appSettings {
            self.settingsSection(
                    isRemote ? self.tr("ui.section.local_app_settings") : self.tr("ui.section.basic_settings"))
            {
                ForEach(localOnlyItems, id: \.id) { item in
                    self.settingsToggleRow(
                        isRemote ? "\(item.title) (\(self.tr("ui.machine.local_label")))" : item.title,
                        symbol: item.symbol,
                        isOn: item.isOn)
                }
                self.settingsSelectionRow(.init(
                    title: self.tr("ui.settings.menu_bar_style"),
                    symbol: "menubar.rectangle",
                    valueText: self.statusBarModeLabel(self.appViewModel.statusBarDisplayMode),
                    options: StatusBarDisplayMode.allCases,
                    optionTitle: self.statusBarModeLabel,
                    isSelected: { self.appViewModel.statusBarDisplayMode == $0 },
                    onSelect: { self.appViewModel.statusBarDisplayMode = $0 }))
                self.settingsToggleRow(
                    self.tr("ui.settings.show_traffic_overview"),
                    symbol: "chart.xyaxis.line",
                    isOn: self.$showTrafficOverview)
                self.settingsSelectionRow(.init(
                    title: self.tr("ui.settings.language"),
                    symbol: "character.book.closed",
                    valueText: self.appViewModel.uiLanguage == .zhHans ? self.tr("ui.language.zh_hans") : self
                        .tr("ui.language.en"),
                    options: AppLanguage.allCases,
                    optionTitle: { $0 == .zhHans ? self.tr("ui.language.zh_hans") : self.tr("ui.language.en") },
                    isSelected: { self.appViewModel.uiLanguage == $0 },
                    onSelect: self.appViewModel.setUILanguage))
                self.settingsSelectionRow(.init(
                    title: self.tr("ui.settings.appearance"),
                    symbol: "circle.lefthalf.filled",
                    valueText: self.appearanceModeLabel(self.appViewModel.appearanceMode),
                    options: AppAppearanceMode.allCases,
                    optionTitle: self.appearanceModeLabel,
                    isSelected: { self.appViewModel.appearanceMode == $0 },
                    onSelect: self.appViewModel.setAppearanceMode))
            }
            }

            if self.scope == .network {
                self.settingsSection(systemProxyExceptionsTitle) {
                Button {
                    self.isExceptionsExpanded.toggle()
                } label: {
                    HStack(spacing: T.space8) {
                        self.settingsRowLabel(
                            symbol: "arrowshape.turn.up.right.circle",
                            title: systemProxyExceptionsTitle)
                            .layoutPriority(1)
                        Spacer(minLength: 0)
                        HStack(spacing: T.space2) {
                            let count = self.appViewModel.systemProxyExceptions.count
                            if count > 0 {
                                Text("\(count)")
                                    .foregroundStyle(nativeSecondaryLabel)
                                    .lineLimit(1)
                            }
                            Image(systemName: "chevron.right")
                                .font(.app(size: T.FontSize.caption, weight: .semibold))
                                .foregroundStyle(nativeTertiaryLabel)
                                .rotationEffect(.degrees(self.isExceptionsExpanded ? 90 : 0))
                        }
                        .font(.app(size: T.FontSize.caption, weight: .medium))
                    }
                    .menuRowPadding(vertical: T.space4)
                    .contentShape(Rectangle())
                }
                .buttonStyle(RowButtonStyle())

                VStack(alignment: .leading, spacing: T.space4) {
                    Text(self.tr("ui.settings.system_proxy_exceptions_hint"))
                        .font(.app(size: T.FontSize.caption, weight: .regular))
                        .foregroundStyle(nativeSecondaryLabel)
                        .fixedSize(horizontal: false, vertical: true)

                    if self.appViewModel.systemProxyExceptions.isEmpty {
                        Text(self.tr("ui.settings.system_proxy_exceptions_empty"))
                            .font(.app(size: T.FontSize.body, weight: .regular))
                            .foregroundStyle(nativeSecondaryLabel)
                    } else {
                        VStack(alignment: .leading, spacing: T.space4) {
                            ForEach(self.appViewModel.systemProxyExceptions) { item in
                                self.systemProxyExceptionRow(item)
                            }
                        }
                    }

                    HStack(spacing: T.space4) {
                        TextField(
                            self.tr("ui.placeholder.system_proxy_exception"),
                            text: self.$appViewModel.systemProxyNewException)
                            .appTextField()
                            .onSubmit {
                                self.appViewModel.addSystemProxyExceptionDraft()
                            }

                        Button(self.tr("ui.action.add_exception")) {
                            self.appViewModel.addSystemProxyExceptionDraft()
                        }
                        .appBorderedButtonStyle()
                        .controlSize(.small)
                        .disabled(!self.appViewModel.canAddSystemProxyException || self
                            .isSystemProxyExceptionsSyncing)
                    }

                    HStack(spacing: T.space4) {
                        Button {
                            self.appViewModel.restoreDefaultSystemProxyExceptionsDraft()
                        } label: {
                            Label(self.tr("ui.action.restore_defaults"), systemImage: "arrow.counterclockwise")
                                .frame(maxWidth: .infinity, alignment: .center)
                        }
                        .appBorderedButtonStyle()
                        .controlSize(.small)
                        .disabled(self.isSystemProxyExceptionsSyncing)

                        Button {
                            Task { await self.appViewModel.saveSystemProxyExceptions() }
                        } label: {
                            Label(self.tr("ui.action.save_exceptions"), systemImage: "checkmark.circle")
                                .frame(maxWidth: .infinity, alignment: .center)
                        }
                        .appBorderedButtonStyle()
                        .controlSize(.small)
                        .disabled(self.isSystemProxyExceptionsSyncing)
                    }
                }
                .menuRowPadding(vertical: T.space4)
                .frame(maxHeight: self.isExceptionsExpanded ? .infinity : 0, alignment: .top)
                .clipped()
                .opacity(self.isExceptionsExpanded ? 1 : 0)
            }
            .animation(.spring(response: 0.30, dampingFraction: 0.80), value: self.isExceptionsExpanded)

            self.settingsSection(self.tr("ui.section.proxy_guard")) {
                self.settingsToggleRow(
                    self.tr("ui.settings.proxy_guard_enabled"),
                    symbol: "shield.checkerboard",
                    isOn: self.$appViewModel.proxyGuardEnabled)

                if self.appViewModel.proxyGuardEnabled {
                    VStack(alignment: .leading, spacing: T.space4) {
                        self.settingsPortFieldRow(
                            self.tr("ui.settings.proxy_guard_interval"),
                            symbol: "timer",
                            text: self.proxyGuardIntervalText)
                        Button {
                            Task { await self.appViewModel.proxyGuardProbe() }
                        } label: {
                            Label(
                                self.appViewModel.isProxyGuardProbing
                                    ? self.tr("ui.action.proxy_guard_check_running")
                                    : self.tr("ui.action.proxy_guard_check_now"),
                                systemImage: "bolt.badge.clock")
                                .frame(maxWidth: .infinity, alignment: .center)
                        }
                        .appBorderedButtonStyle()
                        .controlSize(.small)
                        .disabled(!self.appViewModel.isRuntimeRunning || self.appViewModel.isProxyGuardProbing)

                        if let outcome = self.appViewModel.proxyGuardLastProbeOutcome {
                            self.proxyGuardOutcomeRow(outcome)
                        }
                    }
                    .menuRowPadding(vertical: T.space4)
                } else {
                    HStack(spacing: T.space6) {
                        Image(systemName: "info.circle")
                            .foregroundStyle(nativeTertiaryLabel)
                        Text(self.tr("ui.settings.proxy_guard_disabled_hint"))
                            .font(.app(size: T.FontSize.caption, weight: .regular))
                            .foregroundStyle(nativeSecondaryLabel)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .menuRowPadding(vertical: T.space4)
                }
            }
            .onChange(of: self.appViewModel.proxyGuardEnabled) { enabled in
                if enabled {
                    self.appViewModel.startProxyGuardIfNeeded()
                } else {
                    self.appViewModel.stopProxyGuard()
                }
            }

            // 内网前置代理（Issue #2）：置灰不收起（高度恒定），字段回车确认后重载生效。
            self.settingsSection(self.tr("ui.section.corp_proxy")) {
                self.settingsToggleRow(
                    self.tr("ui.settings.corp_proxy_enabled"),
                    symbol: "building.columns",
                    isOn: self.$appViewModel.corpProxyEnabled)

                VStack(alignment: .leading, spacing: T.space4) {
                    HStack(spacing: T.space8) {
                        self.settingsRowLabel(symbol: "server.rack", title: self.tr("ui.settings.corp_proxy_host"))
                            .layoutPriority(1)
                        Spacer(minLength: 0)
                        TextField(self.tr("ui.placeholder.corp_proxy_host"), text: self.$appViewModel.corpProxyHost)
                            .appTextField()
                            .frame(width: 180, alignment: .trailing)
                            .onSubmit {
                                Task { await self.appViewModel.reloadForCorpProxyChange() }
                            }
                    }

                    self.settingsPortFieldRow(
                        self.tr("ui.settings.corp_proxy_port"),
                        symbol: "number",
                        text: self.corpProxyPortText)

                    HStack(spacing: T.space8) {
                        self.settingsRowLabel(symbol: "person.text.rectangle", title: self.tr("ui.settings.corp_proxy_username"))
                            .layoutPriority(1)
                        Spacer(minLength: 0)
                        TextField(self.tr("ui.placeholder.corp_proxy_optional"), text: self.$appViewModel.corpProxyUsername)
                            .appTextField()
                            .frame(width: 180, alignment: .trailing)
                            .onSubmit {
                                Task { await self.appViewModel.reloadForCorpProxyChange() }
                            }
                    }

                    HStack(spacing: T.space8) {
                        self.settingsRowLabel(symbol: "lock.rectangle", title: self.tr("ui.settings.corp_proxy_password"))
                            .layoutPriority(1)
                        Spacer(minLength: 0)
                        SecureField(self.tr("ui.placeholder.corp_proxy_optional"), text: self.$appViewModel.corpProxyPassword)
                            .appTextField()
                            .frame(width: 180, alignment: .trailing)
                            .onSubmit {
                                Task { await self.appViewModel.reloadForCorpProxyChange() }
                            }
                    }

                    HStack(spacing: T.space6) {
                        Image(systemName: "info.circle")
                            .foregroundStyle(nativeTertiaryLabel)
                        Text(self.tr("ui.settings.corp_proxy_hint"))
                            .font(.app(size: T.FontSize.caption, weight: .regular))
                            .foregroundStyle(nativeSecondaryLabel)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
                .menuRowPadding(vertical: T.space4)
                .opacity(self.appViewModel.corpProxyEnabled ? 1 : 0.5)
                .disabled(!self.appViewModel.corpProxyEnabled)
            }
            .onChange(of: self.appViewModel.corpProxyEnabled) { _ in
                Task { await self.appViewModel.reloadForCorpProxyChange() }
            }

            self.settingsSection(
                    isRemote ? self.tr("ui.section.core_settings_remote") : self.tr("ui.section.core_settings"))
            {
                ForEach(coreToggleItems, id: \.id) { item in
                    self.settingsToggleRow(
                        item.title,
                        symbol: item.symbol,
                        isOn: item.isOn,
                        isDisabled: self.appViewModel.isCoreSettingSyncing)
                }
                // Stored by the app, but it closes connections on whichever core is active, so it
                // sits with the core settings rather than the local-only ones.
                self.settingsToggleRow(
                    self.tr("ui.settings.close_connections_on_proxy_switch"),
                    symbol: "bolt.horizontal.circle",
                    isOn: self.$appViewModel.closeConnectionsOnProxySwitch)
                self.settingsSelectionRow(.init(
                    title: self.tr("ui.settings.log_level"),
                    symbol: "text.alignleft",
                    valueText: selectedLogLevel,
                    options: ConfigLogLevel.allCases,
                    optionTitle: \.rawValue,
                    isSelected: { selectedLogLevel.caseInsensitiveCompare($0.rawValue) == .orderedSame },
                    onSelect: { level in
                        Task { await self.appViewModel.applyEditableCoreSetting(.logLevel, to: level.rawValue) }
                    }))
            }

            self.settingsSection(
                    self.tr("ui.section.proxy_ports"))
            {

                VStack(alignment: .leading, spacing: T.space4) {
                    ForEach(proxyPortFields, id: \.titleKey) { item in
                        self.settingsPortFieldRow(
                            self.tr(item.titleKey),
                            symbol: item.symbol,
                            text: item.text)
                    }
                }
                .menuRowPadding(vertical: T.space4)
            }
            }

            if self.scope == .appSettings {
            self.settingsSection(
                    self.tr("ui.section.maintenance"))
            {

                VStack(alignment: .leading, spacing: T.space4) {
                    HStack(spacing: T.space6) {
                        ForEach(maintenanceActions, id: \.titleKey) { item in
                            self.maintenanceActionButton(self.tr(item.titleKey), symbol: item.symbol) {
                                await item.action()
                            }
                        }
                    }

                    HStack(spacing: T.space6) {
                        Button {
                            Task { await self.appViewModel.upgradeGeo() }
                        } label: {
                            HStack(spacing: T.space4) {
                                Group {
                                    if self.appViewModel.isGeoUpdateInFlight {
                                        ProgressView()
                                            .controlSize(.mini)
                                    } else if let symbol = self.geoUpdateFeedbackSymbol {
                                        Image(systemName: symbol)
                                            .foregroundStyle(self.geoUpdateFeedbackColor)
                                    } else {
                                        Image(systemName: "arrow.down.circle")
                                    }
                                }
                                .frame(width: 16, height: 16)
                                Text(self.tr("ui.action.update_geo_database"))
                            }
                            .frame(maxWidth: .infinity, alignment: .center)
                        }
                        .appBorderedButtonStyle()
                        .controlSize(.small)
                        .disabled(!self.geoUpdateActionEnabled)
                        .opacity(self.geoUpdateActionEnabled ? 1 : 0.62)

                        Button {
                            self.appViewModel.showCoreDirectoryInFinder()
                        } label: {
                            Label(self.tr("ui.action.open_core_directory"), systemImage: "folder")
                                .frame(maxWidth: .infinity, alignment: .center)
                        }
                        .appBorderedButtonStyle()
                        .controlSize(.small)
                    }
                }
                .menuRowPadding(vertical: T.space4)
            }
            }
        }
        .overlay(alignment: .top) {
            if let feedback = settingsFeedbackState {
                self.settingsFeedbackBanner(
                    text: feedback.message,
                    color: feedback.color,
                    symbol: feedback.symbol)
            }
        }
    }
}
