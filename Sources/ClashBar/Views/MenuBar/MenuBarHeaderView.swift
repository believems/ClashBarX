import SwiftUI

// swiftlint:disable:next type_name
private typealias T = MenuBarLayoutTokens

struct MenuBarHeaderView: TranslatingView {
    @EnvironmentObject var appViewModel: AppViewModel
    @EnvironmentObject var remoteMachineStore: RemoteMachineStore
    @Binding var showRemoteMachineManager: Bool
    @Binding var isSwitchingMachine: Bool
    @Environment(\.openURL) private var openURL

    var headerLogoSize: CGFloat {
        36
    }

    var body: some View {
        self.topHeader
    }

    var topHeader: some View {
        HStack(alignment: .center, spacing: T.space8) {
            HStack(alignment: .center, spacing: T.space8) {
                Button {
                    guard let url = URL(string: "https://github.com/believems/ClashBarX") else { return }
                    self.openURL(url)
                } label: {
                    ZStack {
                        if let brandImage = BrandIcon.image {
                            Image(nsImage: brandImage)
                                .resizable()
                                .interpolation(.high)
                                .scaledToFit()
                                .frame(width: self.headerLogoSize, height: self.headerLogoSize)
                        } else {
                            Image(systemName: "paperplane.fill")
                                .renderingMode(.template)
                                .symbolRenderingMode(.monochrome)
                                .resizable()
                                .scaledToFit()
                                .frame(width: self.headerLogoSize, height: self.headerLogoSize)
                                .foregroundStyle(nativeAccent)
                        }
                    }
                    .frame(width: self.headerLogoSize, height: self.headerLogoSize)
                }
                .buttonStyle(PressableButtonStyle())
                .help(self.tr("ui.action.open_project_homepage"))
                .accessibilityLabel(self.tr("ui.action.open_project_homepage"))

                VStack(alignment: .leading, spacing: T.space2) {
                    Text("ClashBarX")
                        .font(.app(size: T.FontSize.title3, weight: .semibold))
                        .foregroundStyle(nativePrimaryLabel)

                    HStack(spacing: T.space4) {
                        self.headerConnectionControl
                        self.headerControllerWebUIButton
                        if self.appViewModel.isExternalControllerWildcardIPv4 {
                            self.headerControllerWarningIcon
                        }
                    }
                }
            }

            Spacer(minLength: T.space6)

            HStack(spacing: T.space4) {
                self.compactTopIcon(
                    self.appViewModel.isPinned ? "pin.fill" : "pin",
                    label: self.appViewModel.isPinned ? self.tr("ui.action.unpin") : self.tr("ui.action.pin"),
                    isOn: self.appViewModel.isPinned,
                    size: 28)
                {
                    self.appViewModel.togglePinned()
                }

                self.compactTopIcon(
                    "arrow.clockwise",
                    label: self.appViewModel.primaryCoreActionLabel,
                    size: 28)
                {
                    await self.appViewModel.performPrimaryCoreAction()
                }
                .disabled(self.appViewModel.isRemoteTarget || !self.appViewModel.isPrimaryCoreActionEnabled)

                self.compactTopIcon(
                    self.appViewModel.isRuntimeRunning ? "stop.fill" : "play.fill",
                    label: self.appViewModel.isRuntimeRunning ? self.tr("ui.action.stop") : self
                        .tr("app.primary.start"),
                    size: 28)
                {
                    if self.appViewModel.isRuntimeRunning {
                        await self.appViewModel.stopCore()
                    } else {
                        await self.appViewModel.startCore(trigger: .manual)
                    }
                }
                .disabled(self.appViewModel.isRemoteTarget || self.appViewModel.isCoreActionProcessing)

                self.compactTopIcon("power", label: self.tr("ui.action.quit"), warning: true, size: 28) {
                    await self.appViewModel.quitApp()
                }
            }
        }
        .padding(.vertical, T.space8)
        .sheet(isPresented: self.$showRemoteMachineManager) {
            RemoteMachineManagerView(
                store: self.remoteMachineStore,
                localControllerDisplay: self.appViewModel.localExternalControllerDisplay)
            { target in
                self.isSwitchingMachine = true
                Task { @MainActor in
                    await self.appViewModel.switchToMachineTarget(target)
                    self.isSwitchingMachine = false
                }
            }
        }
    }

    var headerConnectionControl: some View {
        AttachedPopoverMenu(
            expandAnchor: false,
            onWillPresent: {
                self.remoteMachineStore.checkAllConnectivity()
            },
            label: { hovered in
                HStack(spacing: T.space4) {
                    if self.isSwitchingMachine {
                        ProgressView()
                            .controlSize(.mini)
                            .frame(width: 10, height: 10)
                    } else if self.headerConnectionIsFailed {
                        // A shape as well as a color, so a failure doesn't rely on red alone.
                        Image(systemName: "exclamationmark.circle.fill")
                            .font(.system(size: 10, weight: .semibold))
                            .foregroundStyle(self.headerConnectionStatusTint)
                            .frame(width: 10, height: 10)
                            .help(self.headerConnectionStatusText)
                            .accessibilityLabel(self.headerConnectionStatusText)
                    } else {
                        Circle()
                            .fill(self.headerConnectionStatusTint)
                            .frame(width: 8, height: 8)
                            .help(self.headerConnectionStatusText)
                            .accessibilityLabel(self.headerConnectionStatusText)
                    }

                    Text(self.headerConnectionDisplayText)
                        .font(.app(size: T.FontSize.caption, weight: .medium))
                        .monospacedDigit()
                        .foregroundStyle(nativePrimaryLabel)
                        .lineLimit(1)
                        .truncationMode(.tail)

                    Image(systemName: "chevron.up.chevron.down")
                        .font(.system(size: 8, weight: .semibold))
                        .foregroundStyle(nativeSecondaryLabel)
                }
                .padding(.horizontal, T.space6)
                .frame(height: 20)
                .background(
                    Capsule(style: .continuous)
                        .fill(InteractiveFill.neutral(hovered: hovered, pressed: false, resting: true)))
                .contentShape(Capsule(style: .continuous))
            },
            content: { dismiss in
                self.headerPopoverSection(self.tr("ui.machine.local_label"))
                AttachedPopoverMenuItem(
                    title: self.tr("ui.machine.local"),
                    leadingSymbol: "desktopcomputer",
                    leadingTint: self.nativeSecondaryLabel,
                    selected: self.remoteMachineStore.activeTarget.isLocal,
                    selectionIndicatorPlacement: .trailing)
                {
                    dismiss()
                    guard !self.remoteMachineStore.activeTarget.isLocal else { return }
                    self.isSwitchingMachine = true
                    Task { @MainActor in
                        await self.appViewModel.switchToMachineTarget(.local)
                        self.isSwitchingMachine = false
                    }
                }

                if !self.remoteMachineStore.machines.isEmpty {
                    AttachedPopoverMenuDivider()
                    self.headerPopoverSection(self.tr("ui.machine.remote_label"))
                }

                ForEach(self.remoteMachineStore.machines) { machine in
                    let status = self.remoteMachineStore.statusFor(machine.id)
                    let isSelected = self.remoteMachineStore.activeTargetID == machine.id
                    AttachedPopoverMenuItem(
                        title: machine.name,
                        leadingSymbol: "network",
                        leadingTint: self.machineStatusTint(status),
                        selected: isSelected,
                        selectionIndicatorPlacement: .trailing)
                    {
                        dismiss()
                        guard !isSelected else { return }
                        self.isSwitchingMachine = true
                        Task { @MainActor in
                            await self.appViewModel.switchToMachineTarget(.remote(machine))
                            self.isSwitchingMachine = false
                        }
                    }
                    .disabled(!status.isConnected && !isSelected)
                }

                AttachedPopoverMenuDivider()
                AttachedPopoverMenuItem(
                    title: self.tr("ui.machine.manage"),
                    leadingSymbol: "slider.horizontal.3",
                    leadingTint: self.nativeSecondaryLabel)
                {
                    dismiss()
                    self.showRemoteMachineManager = true
                }
            })
            .disabled(self.isSwitchingMachine)
    }

    var headerControllerWarningIcon: some View {
        Image(systemName: "exclamationmark.triangle.fill")
            .font(.app(size: T.FontSize.caption, weight: .semibold))
            .foregroundStyle(nativeWarning)
            .help("external-controller is 0.0.0.0 and can be accessed from your LAN.")
            .accessibilityLabel("Warning: external-controller is bound to 0.0.0.0")
    }

    var headerControllerWebUIButton: some View {
        Button {
            self.appViewModel.openControllerWebUI()
        } label: {
            Image(systemName: "safari")
        }
        .buttonStyle(IconButtonStyle(size: 20))
        .help("Open WebUI")
        .accessibilityLabel("Open WebUI")
        .disabled(self.isSwitchingMachine)
    }

    func headerPopoverSection(_ title: String) -> some View {
        Text(title)
            .font(.app(size: T.FontSize.caption, weight: .bold))
            .foregroundStyle(nativeSecondaryLabel)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, T.space6)
            .padding(.top, T.space2)
            .padding(.bottom, T.space1)
            .textCase(.uppercase)
    }

    var machineSwitcherStatus: MachineConnectionStatus? {
        guard case let .remote(machine) = remoteMachineStore.activeTarget else { return nil }
        return self.remoteMachineStore.statusFor(machine.id)
    }

    var headerConnectionDisplayText: String {
        self.appViewModel.externalControllerDisplay
    }

    var headerConnectionIsFailed: Bool {
        if let status = self.machineSwitcherStatus {
            if case .failed = status { return true }
            return false
        }
        return self.appViewModel.runtimeVisualStatus == .failed
    }

    /// The status the dot's color stands for, as text for the tooltip and VoiceOver.
    var headerConnectionStatusText: String {
        if let status = self.machineSwitcherStatus {
            switch status {
            case .unknown: return self.tr("ui.header.status.unknown")
            case .checking: return self.tr("ui.header.status.checking")
            case .connected: return self.tr("ui.header.status.connected")
            case .failed: return self.tr("ui.header.status.connection_failed")
            }
        }
        switch self.appViewModel.runtimeVisualStatus {
        case .runningHealthy: return self.tr("ui.header.status.running")
        case .runningDegraded: return self.tr("ui.header.status.degraded")
        case .starting: return self.tr("ui.header.status.starting")
        case .failed: return self.tr("ui.header.status.failed")
        case .stopped: return self.tr("ui.header.status.stopped")
        }
    }

    var headerConnectionStatusTint: Color {
        if let status = self.machineSwitcherStatus {
            return self.machineStatusTint(status)
        }
        return self.statusColor
    }
}
