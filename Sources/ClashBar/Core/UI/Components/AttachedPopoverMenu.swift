import AppKit
import SwiftUI

// swiftlint:disable:next type_name
private typealias T = MenuBarLayoutTokens

struct AttachedPopoverMenu<Label: View, Content: View>: View {
    let width: CGFloat?
    let maxHeight: CGFloat?
    let expandAnchor: Bool
    let allowsKeyFocus: Bool
    let scrollsContent: Bool
    let onWillPresent: (() -> Void)?
    let label: (_ isHovered: Bool) -> Label
    let content: (_ dismiss: @escaping () -> Void) -> Content

    @State private var isAnchorHovered = false
    @State private var isPopoverRequested = false
    @State private var suppressAutoOpen = false
    @State private var shouldBuildPopoverContent = false
    @State private var popoverOpenTask: Task<Void, Never>?

    init(
        width: CGFloat? = nil,
        maxHeight: CGFloat? = nil,
        expandAnchor: Bool = true,
        allowsKeyFocus: Bool = false,
        scrollsContent: Bool = true,
        onWillPresent: (() -> Void)? = nil,
        @ViewBuilder label: @escaping (_ isHovered: Bool) -> Label,
        @ViewBuilder content: @escaping (_ dismiss: @escaping () -> Void) -> Content)
    {
        self.width = width
        self.maxHeight = maxHeight
        self.expandAnchor = expandAnchor
        self.allowsKeyFocus = allowsKeyFocus
        self.scrollsContent = scrollsContent
        self.onWillPresent = onWillPresent
        self.label = label
        self.content = content
    }

    var body: some View {
        Button(action: self.activateAnchor) {
            self.anchorLabel
        }
        .buttonStyle(PressableButtonStyle())
        .onHover { hovering in
            self.isAnchorHovered = hovering

            if hovering, !self.suppressAutoOpen {
                if self.shouldBuildPopoverContent {
                    self.isPopoverRequested = true
                } else {
                    self.popoverOpenTask?.cancel()
                    self.popoverOpenTask = Task { @MainActor in
                        try? await Task.sleep(nanoseconds: 150_000_000)
                        if !Task.isCancelled, self.isAnchorHovered, !self.suppressAutoOpen {
                            self.requestOpen()
                        }
                    }
                }
            } else {
                self.popoverOpenTask?.cancel()
                self.popoverOpenTask = nil
                self.isPopoverRequested = false
            }
            if !hovering {
                self.suppressAutoOpen = false
            }
        }
        .onDisappear {
            self.popoverOpenTask?.cancel()
            self.popoverOpenTask = nil
        }
        .background(
            SideAttachedPopoverHost(
                anchorHovered: self.$isAnchorHovered,
                popoverRequested: self.$isPopoverRequested,
                suppressAutoOpen: self.$suppressAutoOpen,
                width: self.width,
                maxHeight: self.maxHeight,
                allowsKeyFocus: self.allowsKeyFocus,
                onVisibilityChanged: { visible in
                    self.shouldBuildPopoverContent = visible
                },
                content: self.popoverContent))
    }

    var anchorLabel: some View {
        Group {
            if self.expandAnchor {
                self.label(self.isAnchorHovered)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                self.label(self.isAnchorHovered)
            }
        }
        .contentShape(Rectangle())
    }

    @ViewBuilder
    var popoverContent: some View {
        if self.shouldBuildPopoverContent {
            Group {
                if self.scrollsContent {
                    ScrollView(.vertical) {
                        VStack(alignment: .leading, spacing: 0) {
                            self.content {
                                self.dismissPopover()
                            }
                        }
                    }
                    .scrollIndicators(.never)
                } else {
                    self.content {
                        self.dismissPopover()
                    }
                }
            }
            .frame(width: self.width, alignment: .topLeading)
            .frame(maxHeight: self.maxHeight, alignment: .topLeading)
            .padding(T.space8)
            .rootPanelSurface(cornerRadius: T.panelCornerRadius, stroke: self.nativeSeparator.opacity(0.46))
        }
    }

    private func requestOpen() {
        self.popoverOpenTask?.cancel()
        self.popoverOpenTask = nil
        self.onWillPresent?()
        self.shouldBuildPopoverContent = true
        self.isPopoverRequested = true
    }

    private func activateAnchor() {
        self.popoverOpenTask?.cancel()
        self.popoverOpenTask = nil
        self.requestOpen()
        self.suppressAutoOpen = false
        self.isAnchorHovered = true
    }

    private func dismissPopover() {
        self.popoverOpenTask?.cancel()
        self.popoverOpenTask = nil
        self.shouldBuildPopoverContent = false
        self.isPopoverRequested = false
        self.suppressAutoOpen = true
        self.isAnchorHovered = false
    }
}

struct AttachedPopoverMenuItem: View {
    enum SelectionIndicatorPlacement {
        case leading
        case trailing
    }

    let title: String
    var subtitle: String?
    var leadingSymbol: String?
    var leadingTint: Color = .secondary
    var trailingStatusText: String?
    var trailingStatusTextColor: Color?
    var showLeadingDot: Bool = false
    var selected: Bool = false
    var selectionIndicatorPlacement: SelectionIndicatorPlacement = .leading
    var destructive: Bool = false
    let action: () -> Void
    @State private var isHovered = false

    var body: some View {
        Button {
            self.action()
        } label: {
            HStack(spacing: T.space6) {
                if self.showLeadingDot {
                    Circle()
                        .fill(self.leadingTint)
                        .frame(width: 8, height: 8)
                        .frame(width: 12, alignment: .center)
                } else if let leadingSymbol {
                    Image(systemName: leadingSymbol)
                        .font(.app(size: T.FontSize.caption, weight: .semibold))
                        .foregroundStyle(self.leadingTint)
                        .frame(width: 12, alignment: .center)
                } else if self.selectionIndicatorPlacement == .leading {
                    Image(systemName: "checkmark")
                        .font(.app(size: T.FontSize.caption, weight: .semibold))
                        .opacity(self.selected ? 1 : 0)
                        .frame(width: 12, alignment: .center)
                } else {
                    Color.clear
                        .frame(width: 12, height: 12)
                }
                VStack(alignment: .leading, spacing: T.space1) {
                    Text(self.title)
                        .font(.app(size: T.FontSize.body, weight: .medium))
                        .lineLimit(1)
                        .truncationMode(.middle)
                    if let subtitle, !subtitle.isEmpty {
                        Text(subtitle)
                            .font(.app(size: T.FontSize.caption, weight: .regular))
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .foregroundStyle(self.isHovered ? self.nativeSelectedMenuText
                                .opacity(0.9) : self.nativeSecondaryLabel)
                    }
                }
                Spacer(minLength: 0)
                if let trailingStatusText = self.trailingStatusText?.trimmedNonEmpty {
                    Text(trailingStatusText)
                        .font(.app(size: T.FontSize.caption, weight: .regular))
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .foregroundStyle(self.trailingStatusForeground)
                }
                if self.selected, self.selectionIndicatorPlacement == .trailing {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.app(size: T.FontSize.caption, weight: .semibold))
                        .symbolRenderingMode(.hierarchical)
                        .foregroundStyle(self.trailingSelectionForeground)
                }
            }
            .foregroundStyle(self.itemForeground)
            .padding(.horizontal, T.space6)
            .padding(.vertical, T.space2)
            .background(
                RoundedRectangle(cornerRadius: T.cornerRadius, style: .continuous)
                    .fill(self.itemBackground))
            .contentShape(Rectangle())
        }
        .buttonStyle(.borderless)
        .onHover { self.isHovered = $0 }
    }

    private var itemForeground: Color {
        if self.isHovered {
            return self.nativeSelectedMenuText
        }
        return self.destructive ? .red : self.nativePrimaryLabel
    }

    private var itemBackground: Color {
        self.isHovered ? self.nativeMenuHighlightFill : .clear
    }

    private var trailingSelectionForeground: Color {
        if self.isHovered {
            return self.nativeSelectedMenuText
        }
        // Native menus draw the checkmark in the label color, not the accent.
        return self.nativePrimaryLabel
    }

    private var trailingStatusForeground: Color {
        if self.isHovered {
            return self.nativeSelectedMenuText.opacity(0.96)
        }
        return self.trailingStatusTextColor ?? self.nativeSecondaryLabel
    }
}

struct AttachedPopoverMenuDivider: View {
    var body: some View {
        Divider()
            .padding(.vertical, T.space2)
    }
}

@MainActor
private protocol SideAttachedPopoverManaging: AnyObject {
    func deactivateAndClose()
    func requestKeyFocus()
    func updatePreferredContentHeight(_ contentHeight: CGFloat)
}

@MainActor
private enum SideAttachedPopoverRegistry {
    static var activeCoordinator: (any SideAttachedPopoverManaging)?
}

@MainActor
enum SideAttachedPopoverFocus {
    static func requestKeyFocus() {
        SideAttachedPopoverRegistry.activeCoordinator?.requestKeyFocus()
    }

    static func updatePreferredContentHeight(_ contentHeight: CGFloat) {
        SideAttachedPopoverRegistry.activeCoordinator?.updatePreferredContentHeight(contentHeight)
    }
}

@MainActor
private struct SideAttachedPopoverHost<Content: View>: NSViewRepresentable {
    @Binding var anchorHovered: Bool
    @Binding var popoverRequested: Bool
    @Binding var suppressAutoOpen: Bool
    let width: CGFloat?
    let maxHeight: CGFloat?
    let allowsKeyFocus: Bool
    let onVisibilityChanged: ((Bool) -> Void)?
    let content: Content

    func makeCoordinator() -> Coordinator {
        Coordinator(
            anchorHovered: self.$anchorHovered,
            popoverRequested: self.$popoverRequested,
            suppressAutoOpen: self.$suppressAutoOpen,
            allowsKeyFocus: self.allowsKeyFocus,
            onVisibilityChanged: self.onVisibilityChanged,
            content: self.content)
    }

    func makeNSView(context: Context) -> NSView {
        NSView(frame: .zero)
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        context.coordinator.anchorHovered = self.$anchorHovered
        context.coordinator.popoverRequested = self.$popoverRequested
        context.coordinator.suppressAutoOpen = self.$suppressAutoOpen
        context.coordinator.width = self.width
        context.coordinator.maxHeight = self.maxHeight
        context.coordinator.updateAllowsKeyFocus(self.allowsKeyFocus)
        context.coordinator.onVisibilityChanged = self.onVisibilityChanged
        context.coordinator.hostingController.rootView = self.content
        context.coordinator.sync(anchorView: nsView)
    }

    static func dismantleNSView(_ nsView: NSView, coordinator: Coordinator) {
        coordinator.closeNow()
    }

    @MainActor
    final class Coordinator: NSObject, SideAttachedPopoverManaging {
        var anchorHovered: Binding<Bool>
        var popoverRequested: Binding<Bool>
        var suppressAutoOpen: Binding<Bool>
        var width: CGFloat?
        var maxHeight: CGFloat?
        var onVisibilityChanged: ((Bool) -> Void)?

        let panel: SideAttachedMenuPanel
        let hostingController: NSHostingController<Content>
        /// Glass wrapper on macOS 26+ (retained here; the panel only holds it while shown).
        private var containerController: NSViewController?
        private let shadowMargin = PanelGlassContainerController.effectiveShadowMargin

        private var allowsKeyFocus: Bool
        private var hasLockedVisibleFrame = false
        private var anchoredTopY: CGFloat?
        private var anchoredOriginX: CGFloat?
        private var anchoredWidth: CGFloat?
        private var anchoredVisibleFrame: CGRect?
        private weak var anchorView: NSView?
        private weak var hostWindow: NSWindow?
        private var panelTrackingArea: NSTrackingArea?
        private var panelHovering = false
        private var localMouseMonitor: Any?
        private var observingResignKey = false
        private var closeWorkItem: DispatchWorkItem?
        private var attachmentSide: PanelAttachmentSide = .right
        private let bridgeVerticalPadding: CGFloat = 14
        private let edgeOverlap: CGFloat = 1
        private let closeDebounce: TimeInterval = 0.10
        private var reportedVisible = false

        init(
            anchorHovered: Binding<Bool>,
            popoverRequested: Binding<Bool>,
            suppressAutoOpen: Binding<Bool>,
            allowsKeyFocus: Bool,
            onVisibilityChanged: ((Bool) -> Void)?,
            content: Content)
        {
            self.anchorHovered = anchorHovered
            self.popoverRequested = popoverRequested
            self.suppressAutoOpen = suppressAutoOpen
            self.allowsKeyFocus = allowsKeyFocus
            self.onVisibilityChanged = onVisibilityChanged
            self.hostingController = NSHostingController(rootView: content)
            self.panel = SideAttachedMenuPanel(
                contentRect: NSRect(x: 0, y: 0, width: 240, height: 160),
                styleMask: [.borderless, .nonactivatingPanel],
                backing: .buffered,
                defer: true)

            super.init()

            self.panel.isReleasedWhenClosed = false
            self.panel.isOpaque = false
            self.panel.backgroundColor = .clear
            // macOS 26+: native glass draws the shadow inside a transparent margin (see
            // PanelGlassContainerController); the system shadow would add a dark outline.
            self.panel.hasShadow = PanelGlassContainerController.effectiveShadowMargin == 0
            self.panel.hidesOnDeactivate = true
            self.panel.level = .statusBar
            self.panel.collectionBehavior = [.transient, .moveToActiveSpace, .ignoresCycle]
            self.panel.allowsKeyWindow = allowsKeyFocus
            self.panel.becomesKeyOnlyIfNeeded = allowsKeyFocus
            self.panel.onCancel = { [weak self] in
                self?.deactivateAndClose()
            }
            let container = PanelGlassContainerController.wrapping(
                self.hostingController,
                cornerRadius: T.panelCornerRadius)
            self.containerController = container
            self.panel.contentViewController = container
            self.panel.acceptsMouseMovedEvents = true

            self.installTrackingAreaIfNeeded()
        }

        func updateAllowsKeyFocus(_ allowsKeyFocus: Bool) {
            guard self.allowsKeyFocus != allowsKeyFocus else { return }
            self.allowsKeyFocus = allowsKeyFocus
            self.panel.allowsKeyWindow = allowsKeyFocus
            self.panel.becomesKeyOnlyIfNeeded = allowsKeyFocus
        }

        func sync(anchorView: NSView) {
            self.anchorView = anchorView
            self.hostWindow = anchorView.window
            self.installTrackingAreaIfNeeded()

            let shouldShow = self.popoverRequested.wrappedValue && !self.suppressAutoOpen.wrappedValue
            if shouldShow {
                self.cancelPendingClose()
                self.showOrUpdate()
                return
            }

            if self.suppressAutoOpen.wrappedValue {
                self.closeNow()
            } else {
                self.evaluateAndCloseIfNeeded()
            }
        }

        private func showOrUpdate() {
            guard
                let anchorView,
                let hostWindow
            else { return }

            self.panel.appearance = hostWindow.effectiveAppearance
            self.hostingController.view.appearance = hostWindow.effectiveAppearance

            let wasVisible = self.panel.isVisible
            let shouldUpdateFrame = !wasVisible || !self.allowsKeyFocus || !self.hasLockedVisibleFrame
            if shouldUpdateFrame {
                self.hostingController.view.layoutSubtreeIfNeeded()
                let fitting = self.hostingController.view.fittingSize
                guard let anchorContext = PanelAnchorContext.resolve(for: anchorView) else { return }
                guard let placement = anchorContext.sideAttachedPlacement(
                    contentSize: fitting,
                    // `width` sizes the content; the surface adds `T.space8` padding on each side.
                    preferredWidth: self.width.map { $0 + T.space8 * 2 },
                    maximumHeight: self.maxHeight,
                    screenPadding: 6,
                    edgeOverlap: self.edgeOverlap)
                else {
                    return
                }

                if let attachmentSide = placement.attachmentSide {
                    self.attachmentSide = attachmentSide
                }

                // Placement is for the visible glass; the window extends by the shadow margin.
                self.panel.setFrame(
                    placement.frame.insetBy(dx: -self.shadowMargin, dy: -self.shadowMargin),
                    display: true)
                self.anchoredTopY = placement.frame.maxY
                self.anchoredOriginX = placement.frame.origin.x
                self.anchoredWidth = placement.frame.width
                self.anchoredVisibleFrame = anchorContext.visibleFrame
                if self.allowsKeyFocus, fitting.height > 40 {
                    self.hasLockedVisibleFrame = true
                }
            }

            self.ensureExclusiveVisibility()

            if !wasVisible {
                self.panel.orderFront(nil)
            }
            ScrollIndicatorPolicy.suppressRecursively(in: self.panel.contentView)
            self.reportVisibility(true)
            self.startLocalMouseMonitorIfNeeded()
            self.startResignKeyObserverIfNeeded()
        }

        private func evaluateAndCloseIfNeeded() {
            guard self.panel.isVisible else {
                self.cancelPendingClose()
                self.stopLocalMouseMonitor()
                self.stopResignKeyObserver()
                return
            }
            if self.panelHovering {
                self.cancelPendingClose()
                return
            }
            let mouse = NSEvent.mouseLocation
            if self.isMouseInActiveArea(mouse) {
                self.cancelPendingClose()
                return
            }
            self.scheduleClose()
        }

        func closeNow() {
            let wasKeyWindow = self.panel.isKeyWindow
            self.cancelPendingClose()
            self.stopLocalMouseMonitor()
            self.stopResignKeyObserver()
            if wasKeyWindow {
                self.panel.makeFirstResponder(nil)
            }
            if self.panel.isVisible {
                self.panel.orderOut(nil)
            }
            if wasKeyWindow, let hostWindow = self.hostWindow, hostWindow.isVisible {
                hostWindow.makeKey()
            }
            self.hasLockedVisibleFrame = false
            self.anchoredTopY = nil
            self.anchoredOriginX = nil
            self.anchoredWidth = nil
            self.anchoredVisibleFrame = nil
            self.panelHovering = false
            self.reportVisibility(false)
            if SideAttachedPopoverRegistry.activeCoordinator === self {
                SideAttachedPopoverRegistry.activeCoordinator = nil
            }
        }

        func requestKeyFocus() {
            guard self.allowsKeyFocus, self.panel.isVisible else { return }
            if !NSApp.isActive {
                NSApp.activate(ignoringOtherApps: true)
            }
            if !self.panel.isKeyWindow {
                self.panel.makeKey()
            }
        }

        func updatePreferredContentHeight(_ contentHeight: CGFloat) {
            guard
                self.allowsKeyFocus,
                self.panel.isVisible,
                contentHeight > 0,
                let anchoredTopY = self.anchoredTopY,
                let anchoredOriginX = self.anchoredOriginX,
                let anchoredWidth = self.anchoredWidth,
                let visibleFrame = self.anchoredVisibleFrame
            else {
                return
            }

            let screenPadding: CGFloat = 6
            let desiredHeight = contentHeight + T.space8 * 2
            let maxScreenHeight = max(40, visibleFrame.height - (screenPadding * 2))
            let cappedHeight = self.maxHeight.map { min(desiredHeight, $0 + (screenPadding * 2)) } ?? desiredHeight
            let targetHeight = min(max(40, cappedHeight), maxScreenHeight)

            let minY = visibleFrame.minY + screenPadding
            let maxTopY = visibleFrame.maxY - screenPadding
            let clampedTopY = min(anchoredTopY, maxTopY)
            var targetOriginY = clampedTopY - targetHeight
            if targetOriginY < minY {
                targetOriginY = minY
            }

            let visibleRect = NSRect(
                x: anchoredOriginX,
                y: targetOriginY,
                width: anchoredWidth,
                height: targetHeight)
            let targetPanelFrame = visibleRect.insetBy(dx: -self.shadowMargin, dy: -self.shadowMargin)

            let heightChanged = abs(self.panel.frame.height - targetPanelFrame.height) > 0.5
            let originYChanged = abs(self.panel.frame.origin.y - targetPanelFrame.origin.y) > 0.5
            guard heightChanged || originYChanged else { return }

            self.panel.setFrame(targetPanelFrame, display: true, animate: false)
            self.panelHovering = visibleRect.contains(NSEvent.mouseLocation)
            ScrollIndicatorPolicy.suppressRecursively(in: self.panel.contentView)
        }

        private func reportVisibility(_ visible: Bool) {
            guard self.reportedVisible != visible else { return }
            self.reportedVisible = visible
            guard let onVisibilityChanged else { return }
            DispatchQueue.main.async {
                onVisibilityChanged(visible)
            }
        }

        private func ensureExclusiveVisibility() {
            if let active = SideAttachedPopoverRegistry.activeCoordinator, active !== self {
                active.deactivateAndClose()
            }
            SideAttachedPopoverRegistry.activeCoordinator = self
        }

        func deactivateAndClose() {
            self.anchorHovered.wrappedValue = false
            self.popoverRequested.wrappedValue = false
            self.closeNow()
        }

        private func scheduleClose() {
            guard self.closeWorkItem == nil else { return }
            let work = DispatchWorkItem { [weak self] in
                guard let self else { return }
                self.closeWorkItem = nil

                guard self.panel.isVisible else { return }
                if self.panelHovering {
                    return
                }

                let mouse = NSEvent.mouseLocation
                if self.isMouseInActiveArea(mouse) {
                    return
                }

                self.closeNow()
            }
            self.closeWorkItem = work
            DispatchQueue.main.asyncAfter(deadline: .now() + self.closeDebounce, execute: work)
        }

        private func cancelPendingClose() {
            self.closeWorkItem?.cancel()
            self.closeWorkItem = nil
        }

        private func startLocalMouseMonitorIfNeeded() {
            guard self.localMouseMonitor == nil else { return }
            self.localMouseMonitor = NSEvent.addLocalMonitorForEvents(
                matching: [.mouseMoved, .leftMouseDragged, .rightMouseDragged])
            { [weak self] event in
                self?.evaluateAndCloseIfNeeded()
                return event
            }
        }

        private func stopLocalMouseMonitor() {
            guard let localMouseMonitor else { return }
            NSEvent.removeMonitor(localMouseMonitor)
            self.localMouseMonitor = nil
        }

        private func startResignKeyObserverIfNeeded() {
            guard self.allowsKeyFocus, !self.observingResignKey else { return }
            self.observingResignKey = true
            NotificationCenter.default.addObserver(
                self,
                selector: #selector(self.handlePanelDidResignKey(_:)),
                name: NSWindow.didResignKeyNotification,
                object: self.panel)
        }

        private func stopResignKeyObserver() {
            guard self.observingResignKey else { return }
            self.observingResignKey = false
            NotificationCenter.default.removeObserver(
                self,
                name: NSWindow.didResignKeyNotification,
                object: self.panel)
        }

        @objc
        private func handlePanelDidResignKey(_ notification: Notification) {
            self.evaluateAndCloseIfNeeded()
        }

        private func isMouseInActiveArea(_ point: NSPoint) -> Bool {
            guard let rects = activityRects() else { return false }
            return rects.trigger.contains(point) || rects.panel.contains(point) || rects.bridge.contains(point)
        }

        private func activityRects() -> (trigger: NSRect, panel: NSRect, bridge: NSRect)? {
            guard self.panel.isVisible else { return nil }
            guard let anchorView, let hostWindow else { return nil }

            let triggerRectInWindow = anchorView.convert(anchorView.bounds, to: nil)
            let triggerRect = hostWindow.convertToScreen(triggerRectInWindow)
            let panelRect = self.panel.frame.insetBy(dx: self.shadowMargin, dy: self.shadowMargin)

            let bridgeMinY = min(triggerRect.minY - self.bridgeVerticalPadding, panelRect.minY)
            let bridgeMaxY = max(triggerRect.maxY + self.bridgeVerticalPadding, panelRect.maxY)
            let bridgeHeight = max(0, bridgeMaxY - bridgeMinY)

            let bridgeRect: NSRect
            switch self.attachmentSide {
            case .right:
                let bridgeX = triggerRect.maxX
                let bridgeWidth = max(0, panelRect.minX - triggerRect.maxX)
                bridgeRect = NSRect(x: bridgeX, y: bridgeMinY, width: bridgeWidth, height: bridgeHeight)
            case .left:
                let bridgeX = panelRect.maxX
                let bridgeWidth = max(0, triggerRect.minX - panelRect.maxX)
                bridgeRect = NSRect(x: bridgeX, y: bridgeMinY, width: bridgeWidth, height: bridgeHeight)
            }

            return (triggerRect, panelRect, bridgeRect)
        }

        private func installTrackingAreaIfNeeded() {
            let view = self.hostingController.view
            if let panelTrackingArea {
                view.removeTrackingArea(panelTrackingArea)
            }
            let options: NSTrackingArea.Options = [.activeAlways, .mouseEnteredAndExited, .inVisibleRect]
            let tracking = NSTrackingArea(rect: .zero, options: options, owner: self, userInfo: nil)
            view.addTrackingArea(tracking)
            panelTrackingArea = tracking
        }

        @objc(mouseEntered:)
        func mouseEntered(_ event: NSEvent) {
            self.panelHovering = true
            self.cancelPendingClose()
        }

        @objc(mouseExited:)
        func mouseExited(_ event: NSEvent) {
            self.panelHovering = false
            self.evaluateAndCloseIfNeeded()
        }
    }
}

@MainActor
private final class SideAttachedMenuPanel: NSPanel {
    var allowsKeyWindow = false
    var onCancel: (() -> Void)?

    override var canBecomeKey: Bool {
        self.allowsKeyWindow
    }

    override var canBecomeMain: Bool {
        false
    }

    override func cancelOperation(_ sender: Any?) {
        self.onCancel?()
    }
}
