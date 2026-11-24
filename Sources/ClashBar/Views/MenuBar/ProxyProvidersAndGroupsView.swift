import SwiftUI

// swiftlint:disable:next type_name
private typealias T = MenuBarLayoutTokens

extension ProxyTabView {
    var proxyProvidersSection: some View {
        let providers = proxyStore.sortedProxyProviderNames

        return VStack(alignment: .leading, spacing: T.space2) {
            self.nodesSectionHeader(
                tr("ui.section.proxy_providers"),
                count: "\(providers.count)")
            {
                let label = tr(isProxyProvidersCollapsed ? "ui.action.expand" : "ui.action.collapse")
                HStack(spacing: T.space2) {
                    if !providers.isEmpty {
                        self.compactTopIcon(
                            "arrow.clockwise",
                            label: tr("ui.action.update_all_proxy_providers"),
                            isLoading: proxyStore.isProxyProvidersRefreshing,
                            size: 22)
                        {
                            await appViewModel.refreshAllProxyProviders()
                        }
                    }

                    Button {
                        withAnimation(.easeInOut(duration: 0.16)) {
                            isProxyProvidersCollapsed.toggle()
                        }
                    } label: {
                        Image(systemName: isProxyProvidersCollapsed ? "chevron.right" : "chevron.down")
                    }
                    .buttonStyle(IconButtonStyle(size: 22, showsRestingFill: false))
                    .accessibilityLabel(label)
                    .help(label)
                }
            }

            if providers.isEmpty {
                emptyCard(tr("ui.empty.proxy_providers"))
            } else if !isProxyProvidersCollapsed {
                VStack(spacing: T.space2) {
                    ForEach(providers, id: \.self) { name in
                        self.proxyProviderRow(name: name, detail: proxyStore.proxyProvidersDetail[name])
                    }
                }
                .groupedSection()
            }
        }
    }

    func proxyProviderRow(name: String, detail: ProviderDetail?) -> some View {
        let nodeCount = detail?.proxies?.count ?? 0
        let updatedText = ValueFormatter.relativeTime(from: detail?.updatedAt, language: language)
        let expireSeconds = detail?.subscriptionInfo?.expire
        let expireText = ValueFormatter.daysUntilExpiryShort(from: expireSeconds, language: language)
        let expiresSoon = expireSeconds.map { $0 > 0 && Double($0) - Date().timeIntervalSince1970 < 15 * 86400 } ?? false
        let expireColor: Color = expiresSoon ? nativeWarning : nativeSecondaryLabel
        let upload = detail?.subscriptionInfo?.upload
        let download = detail?.subscriptionInfo?.download
        let total = detail?.subscriptionInfo?.total
        let usedRatio: Double? = {
            guard let total, total > 0, let upload, let download else { return nil }
            let used = upload + download
            return min(max(Double(used) / Double(total), 0), 1)
        }()
        let rowHorizontalPadding = T.space6
        let isUpdating = proxyStore.providerUpdating.contains(name)
        let updateTimeWidth: CGFloat = 56

        let hasSubscription = detail?.subscriptionInfo != nil

        return VStack(alignment: .leading, spacing: T.space6) {
            HStack(alignment: .center, spacing: T.space6) {
                Image(systemName: "externaldrive")
                    .font(.system(size: T.FontSize.callout, weight: .regular))
                    .foregroundStyle(nativeSecondaryLabel)
                    .frame(width: T.rowLeadingIcon, height: T.rowLeadingIcon)

                HStack(alignment: .center, spacing: T.space4) {
                    HStack(alignment: .center, spacing: T.space4) {
                        Text(name)
                            .font(.app(size: T.FontSize.body, weight: .semibold))
                            .foregroundStyle(nativePrimaryLabel)
                            .lineLimit(1)
                            .layoutPriority(1)

                        Text("\(nodeCount)")
                            .font(.app(size: T.FontSize.caption, weight: .medium))
                            .monospacedDigit()
                            .foregroundStyle(nativeSecondaryLabel)
                            .fixedSize()
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)

                    Text(updatedText)
                        .font(.app(size: T.FontSize.caption, weight: .regular))
                        .foregroundStyle(nativeSecondaryLabel)
                        .lineLimit(1)
                        .frame(width: updateTimeWidth, alignment: .trailing)
                }
                .frame(maxWidth: .infinity)

                self.providerActionButton(.refresh, isLoading: isUpdating) {
                    await appViewModel.updateProxyProvider(name: name)
                }
            }

            if hasSubscription {
                VStack(alignment: .leading, spacing: T.space2) {
                    HStack(spacing: 0) {
                        Text(expireText)
                            .font(.app(size: T.FontSize.caption, weight: .regular))
                            .foregroundStyle(expireColor)
                        Spacer(minLength: T.space4)
                        if let upload, let download, let total {
                            let used = upload + download
                            let quotaText =
                                "\(ValueFormatter.bytesCompactNoSpace(used)) / " +
                                "\(ValueFormatter.bytesCompactNoSpace(total))"
                            Text(quotaText)
                                .font(.app(size: T.FontSize.caption, weight: .regular))
                                .monospacedDigit()
                                .foregroundStyle(nativeSecondaryLabel)
                                .lineLimit(1)
                        }
                    }

                    if let usedRatio {
                        GeometryReader { geo in
                            ZStack(alignment: .leading) {
                                Capsule().fill(NeutralFill.track)
                                Capsule()
                                    .fill(usedRatio >= 0.9 ? nativeCritical.opacity(T.Opacity.solid)
                                        : usedRatio >= 0.75 ? nativeWarning.opacity(T.Opacity.solid)
                                        : nativeSecondaryLabel)
                                    .frame(width: geo.size.width * usedRatio)
                            }
                        }
                        .frame(height: T.space4)
                    }
                }
                .padding(.leading, T.rowLeadingIcon + T.space6)
                .padding(.trailing, 22 + T.space6)
            }
        }
        .padding(.horizontal, rowHorizontalPadding)
        .padding(.vertical, T.space4)
    }

    enum ProviderAction {
        case healthcheck
        case refresh

        var symbol: String {
            switch self {
            case .healthcheck: "bolt"
            case .refresh: "arrow.clockwise"
            }
        }

        var labelKey: String {
            switch self {
            case .healthcheck: "ui.action.test_latency"
            case .refresh: "ui.action.refresh"
            }
        }
    }

    func providerActionButton(
        _ kind: ProviderAction,
        isLoading: Bool = false,
        action: @escaping () async -> Void) -> some View
    {
        self.compactAsyncIconButton(
            symbol: kind.symbol,
            label: tr(kind.labelKey),
            isLoading: isLoading,
            size: 22,
            showsRestingFill: kind == .refresh,
            action: action)
    }

    var proxyGroupsSection: some View {
        let groups = rootViewModel.filteredProxyGroups

        return VStack(alignment: .leading, spacing: T.space2) {
            self.nodesSectionHeader(
                tr("ui.section.proxy_groups"),
                count: "\(groups.count)")
            {
                HStack(spacing: T.space2) {
                    self.compactTopIcon(
                        "arrow.up.arrow.down",
                        label: tr("ui.action.sort_nodes_by_latency"),
                        isOn: sortGroupNodesByLatency,
                        size: 22)
                    {
                        sortGroupNodesByLatency.toggle()
                    }

                    self.compactTopIcon(
                        "chart.bar",
                        label: tr("ui.action.show_latency_history"),
                        isOn: showLatencyHistory,
                        size: 22)
                    {
                        showLatencyHistory.toggle()
                    }

                    self.compactTopIcon(
                        "eye",
                        label: tr("ui.action.show_hidden_proxy_groups"),
                        isOn: !hideHiddenProxyGroups,
                        size: 22)
                    {
                        hideHiddenProxyGroups.toggle()
                    }

                    self.compactTopIcon(
                        "bolt",
                        label: tr("ui.action.test_latency"),
                        isLoading: proxyStore.isTestingAllGroupLatencies,
                        size: 22)
                    {
                        await appViewModel.refreshAllGroupLatencies(includeHiddenGroups: !hideHiddenProxyGroups)
                    }
                }
            }

            if groups.isEmpty {
                emptyCard(tr("ui.empty.proxy_groups"))
            } else {
                VStack(spacing: 0) {
                    ForEach(groups, id: \.name) { group in
                        self.proxyGroupInlineRow(group)
                    }
                }
                .groupedSection()
            }
        }
    }

    func proxyGroupInlineRow(_ group: ProxyGroup) -> some View {
        let currentNode = group.now ?? tr("ui.common.na")
        let delaySamples = appViewModel.delaySamples(
            group: group.name,
            node: currentNode,
            fallbackToGroupHistory: true)
        let delayValue = delaySamples.last
        let delayText = appViewModel.delayText(
            group: group.name,
            node: currentNode,
            fallbackToGroupHistory: true)
        let nodeCount = group.all.count
        let iconURL = self.proxyGroupIconURL(group)
        let hasLeadingIcon = iconURL != nil
        let rowHorizontalPadding = T.space6
        let rowVerticalPadding: CGFloat = 2
        let isLatencyLoading = proxyStore.groupLatencyLoading.contains(group.name)

        return AttachedPopoverMenu(width: self.nodePopoverContentWidth(group)) { isHovered in
            GeometryReader { geo in
                let columns = self.proxyGroupMainColumnWidths(
                    totalWidth: geo.size.width,
                    hasLeadingIcon: hasLeadingIcon)
                HStack(spacing: T.space1) {
                    if let iconURL {
                        self.proxyGroupLeadingIcon(iconURL)
                    }

                    Text(group.name)
                        .font(.app(size: T.FontSize.body, weight: .medium))
                        .foregroundStyle(nativePrimaryLabel)
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .minimumScaleFactor(T.minimumScale)
                        .frame(width: columns.name, alignment: .leading)

                    Text(currentNode)
                        .font(.app(size: T.FontSize.caption, weight: .regular))
                        .foregroundStyle(nativeSecondaryLabel)
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .minimumScaleFactor(T.minimumScale)
                        .frame(width: columns.current, alignment: .leading)

                    Group {
                        if delayValue != nil {
                            delayBadge(
                                samples: delaySamples,
                                fallbackText: delayText,
                                showsHistory: showLatencyHistory)
                        } else {
                            Text(delayText)
                                .font(.app(size: T.FontSize.caption, weight: .regular))
                                .foregroundStyle(latencyColor(delayValue))
                                .lineLimit(1)
                                .minimumScaleFactor(T.minimumScale)
                        }
                    }
                    // Dimmed while re-testing: the number shown is the previous result.
                    .opacity(isLatencyLoading ? 0.45 : 1)
                    .frame(width: columns.delay, alignment: .trailing)

                    // Chevron at rest hints "opens node list"; hovering swaps in the latency test action.
                    ZStack {
                        if isHovered || isLatencyLoading {
                            self.providerActionButton(.healthcheck, isLoading: isLatencyLoading) {
                                await appViewModel.refreshGroupLatency(group)
                            }
                        } else {
                            Image(systemName: "chevron.right")
                                .font(.system(size: T.FontSize.caption - 1, weight: .semibold))
                                .foregroundStyle(nativeTertiaryLabel)
                        }
                    }
                    .frame(width: 22)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            }
            .frame(height: 22)
            .padding(.leading, rowHorizontalPadding)
            .padding(.trailing, T.space1)
            .padding(.vertical, rowVerticalPadding)
            .background(nativeHoverRowBackground(isHovered))
        } content: { dismiss in
            self.popoverHeader(name: group.name, count: nodeCount) {
                if let iconURL {
                    self.proxyGroupLeadingIcon(iconURL)
                }
            } trailing: {
                self.providerActionButton(
                    .healthcheck,
                    isLoading: proxyStore.groupLatencyLoading.contains(group.name))
                {
                    await appViewModel.refreshGroupLatency(group)
                }
                .help(tr("ui.action.test_latency"))
            }

            let nodes = sortGroupNodesByLatency
                ? self.sortedGroupNodes(group)
                : self.defaultGroupNodes(group)
            FrozenPopoverNodesList(nodes: nodes, emptyText: tr("ui.common.na")) { node in
                let nodeSamples = appViewModel.delaySamples(group: group.name, node: node)
                let nodeDelay = nodeSamples.last
                ProxyGroupPopoverNodeItem(
                    title: node,
                    typeText: proxyStore.proxyNodeTypes[node].trimmedNonEmpty,
                    delayText: appViewModel.delayText(group: group.name, node: node),
                    delaySamples: nodeSamples,
                    delayColor: latencyColor(nodeDelay),
                    isTesting: appViewModel.isProxyLatencyTesting(group: group.name, node: node),
                    selected: node == group.now,
                    testLatencyLabel: tr("ui.action.test_latency"),
                    onTestLatency: {
                        await appViewModel.refreshProxyLatency(group: group.name, node: node)
                    },
                    action: {
                        dismiss()
                        Task { await appViewModel.switchProxy(group: group.name, target: node) }
                    })
            }
        }
    }

    /// Width of the node popover, sized up front from the longest node name so the popover opens
    /// at its final width (a width that settles after opening made it jump sideways).
    func nodePopoverContentWidth(_ group: ProxyGroup) -> CGFloat {
        let nameFont = NSFont.systemFont(ofSize: T.FontSize.body, weight: .semibold)
        let typeFont = NSFont.systemFont(ofSize: T.FontSize.caption, weight: .medium)
        func textWidth(_ text: String, _ font: NSFont) -> CGFloat {
            ceil((text as NSString).size(withAttributes: [.font: font]).width)
        }

        var widest: CGFloat = 0
        for node in group.all {
            var rowWidth = textWidth(node, nameFont)
            if let type = proxyStore.proxyNodeTypes[node].trimmedNonEmpty {
                // Badge: text + horizontal padding + spacing before it.
                rowWidth += textWidth(type, typeFont) + T.space4 * 2 + T.space1
            }
            widest = max(widest, rowWidth)
        }

        // Row chrome: padding, selection mark, spacings, latency column and test slot.
        let chrome: CGFloat = T.space4 * 2 + 11 + T.space1 * 3 + 50 + T.space2 + 20
        let slack: CGFloat = 12
        return min(max(widest + chrome + slack, T.nodePopoverMinWidth), T.nodePopoverMaxWidth)
    }

    func proxyGroupMainColumnWidths(
        totalWidth: CGFloat,
        hasLeadingIcon: Bool) -> (name: CGFloat, current: CGFloat, delay: CGFloat)
    {
        let iconWidth: CGFloat = hasLeadingIcon ? T.rowLeadingIcon : 0
        let actionWidth: CGFloat = 22
        let spacingCount: CGFloat = hasLeadingIcon ? 4 : 3
        let spacing = T.space1 * spacingCount
        let available = max(0, totalWidth - iconWidth - actionWidth - spacing)
        let name = floor(available * 0.38)
        let delay = floor(available * 0.18)
        let current = max(0, available - name - delay)
        return (name, current, delay)
    }

    func proxyGroupLeadingIcon(_ iconURL: URL) -> some View {
        ProxyGroupIconView(url: iconURL)
            .frame(
                width: T.rowLeadingIcon,
                height: T.rowLeadingIcon,
                alignment: .center)
    }

    func proxyGroupIconURL(_ group: ProxyGroup) -> URL? {
        guard let icon = group.icon else { return nil }
        return URL(string: icon)
    }

    func nodesSectionHeader(
        _ title: String,
        count: String? = nil,
        @ViewBuilder trailing: () -> some View = { EmptyView() }) -> some View
    {
        HStack(spacing: T.space6) {
            Text(title)
                .font(.app(size: T.FontSize.caption, weight: .semibold))
                .foregroundStyle(nativeSecondaryLabel)

            if let count {
                Text(count)
                    .font(.app(size: T.FontSize.caption, weight: .medium))
                    .monospacedDigit()
                    .foregroundStyle(nativeSecondaryLabel)
                    .padding(.horizontal, T.space6)
                    .padding(.vertical, T.space1)
                    .background(nativeBadgeCapsule())
            }

            Spacer(minLength: 0)
            trailing()
        }
        .frame(maxWidth: .infinity, minHeight: 26, alignment: .leading)
        .padding(.leading, T.space8)
        .padding(.trailing, T.space4)
    }

    func popoverHeader(
        name: String,
        count: Int,
        @ViewBuilder leading: () -> some View = { EmptyView() },
        @ViewBuilder trailing: () -> some View = { EmptyView() }) -> some View
    {
        VStack(spacing: 0) {
            HStack(spacing: T.space1) {
                leading()

                Text(name)
                    .font(.app(size: T.FontSize.body, weight: .semibold))
                    .foregroundStyle(nativePrimaryLabel)
                    .lineLimit(1)

                Text("\(count)")
                    .font(.app(size: T.FontSize.caption, weight: .medium))
                    .foregroundStyle(nativeSecondaryLabel)
                    .padding(.horizontal, T.space4)
                    .padding(.vertical, T.space1)
                    .background(nativeBadgeCapsule())

                Spacer(minLength: 0)

                trailing()
            }
            .padding(.horizontal, T.space4)
            .padding(.bottom, T.space2)

            Divider()
                .overlay(nativeSeparator)
                .padding(.bottom, T.space1)
        }
    }

    func orderedUniqueNames(_ names: [String]) -> [String] {
        var seen: Set<String> = []
        var ordered: [String] = []
        ordered.reserveCapacity(names.count)
        for name in names where !name.isEmpty {
            if seen.insert(name).inserted {
                ordered.append(name)
            }
        }
        return ordered
    }

    func sortedGroupNodes(_ group: ProxyGroup) -> [String] {
        self.sortedNodes(names: group.all, latencyForNode: { appViewModel.delayValue(group: group.name, node: $0) })
    }

    func defaultGroupNodes(_ group: ProxyGroup) -> [String] {
        let unique = self.orderedUniqueNames(group.all)
        guard appViewModel.hideUnavailableProxyNodes else { return unique }
        return unique.filter { self.isProxyNodeAvailable(appViewModel.delayValue(group: group.name, node: $0)) }
    }

    private func sortedNodes(names: [String], latencyForNode: (String) -> Int?) -> [String] {
        let unique = self.orderedUniqueNames(names)
        let sorted = unique.sorted { lhs, rhs in
            let cmp = self.compareLatency(lhs: latencyForNode(lhs), rhs: latencyForNode(rhs), ascending: true)
            if cmp != .orderedSame {
                return cmp == .orderedAscending
            }
            return lhs.localizedStandardCompare(rhs) == .orderedAscending
        }
        guard appViewModel.hideUnavailableProxyNodes else { return sorted }
        return sorted.filter { self.isProxyNodeAvailable(latencyForNode($0)) }
    }
}

private struct FrozenPopoverNodesList<Row: View>: View {
    let nodes: [String]
    let emptyText: String
    let row: (String) -> Row

    @State private var frozenNodes: [String]

    init(nodes: [String], emptyText: String, @ViewBuilder row: @escaping (String) -> Row) {
        self.nodes = nodes
        self.emptyText = emptyText
        self.row = row
        self._frozenNodes = State(initialValue: nodes)
    }

    var body: some View {
        if self.frozenNodes.isEmpty {
            Text(self.emptyText)
                .font(.app(size: T.FontSize.caption, weight: .regular))
                .foregroundStyle(self.nativeSecondaryLabel)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, T.space6)
                .padding(.vertical, T.space4)
        } else {
            VStack(spacing: 0) {
                ForEach(self.frozenNodes, id: \.self) { node in
                    self.row(node)
                }
            }
        }
    }
}

private struct ProxyGroupPopoverNodeItem: View {
    let title: String
    let typeText: String?
    let delayText: String
    let delaySamples: [Int]
    let delayColor: Color
    let isTesting: Bool
    let selected: Bool
    let testLatencyLabel: String
    let onTestLatency: () async -> Void
    let action: () -> Void

    @State private var isHovered = false
    @AppStorage("clashbar.proxy.group.show_latency_history") private var showLatencyHistory: Bool = false

    private var delayValue: Int? {
        self.delaySamples.last
    }

    var body: some View {
        HStack(spacing: T.space1) {
            Button(action: self.action) {
                HStack(spacing: T.space1) {
                    Image(systemName: self.selected ? "checkmark.circle.fill" : "circle")
                        .font(.app(size: T.FontSize.caption, weight: .semibold))
                        .foregroundStyle(self
                            .selected ? self.nativeAccent : self.nativeTertiaryLabel)
                        .frame(width: 11, alignment: .center)

                    // Fixed popover width: long names truncate and scroll on hover instead of
                    // widening the popover (which shifted it sideways while it settled).
                    MarqueeText(
                        text: self.title,
                        font: .app(size: T.FontSize.body, weight: self.selected ? .semibold : .medium),
                        color: self.selected ? self.nativePrimaryLabel : self.nativeSecondaryLabel,
                        isActive: self.isHovered)

                    if let typeText = self.typeText {
                        Text(typeText)
                            .font(.app(size: T.FontSize.caption, weight: .medium))
                            .foregroundStyle(self.selected ? self.nativePrimaryLabel.opacity(0.72) : self
                                .nativeSecondaryLabel
                                .opacity(0.82))
                            .lineLimit(1)
                            .fixedSize()
                            .padding(.horizontal, T.space4)
                            .padding(.vertical, T.space1)
                            .background(
                                RoundedRectangle(cornerRadius: T.cornerRadius, style: .continuous)
                                    .fill(Color(nsColor: .quaternaryLabelColor).opacity(self.selected ? 0.18 : 0.1)))
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(PressableButtonStyle())

            self.latencyControl
        }
        .frame(height: T.compactRowHeight)
        .padding(.horizontal, T.space4)
        .padding(.vertical, T.space1)
        .background(
            RoundedRectangle(cornerRadius: T.cornerRadius, style: .continuous)
                .fill(self.rowBackground))
        .onHover { self.isHovered = $0 }
    }

    /// The latency stays visible; the test action sits in its own trailing slot (shown on hover)
    /// instead of replacing the number, so you can read the result while re-testing.
    var latencyControl: some View {
        HStack(spacing: T.space2) {
            self.delayMetricView
                .opacity(self.isTesting ? 0.45 : 1)
                .frame(width: 50, alignment: .trailing)

            ZStack {
                if self.isTesting {
                    ProgressView()
                        .controlSize(.mini)
                } else if self.isHovered {
                    Button {
                        Task { await self.onTestLatency() }
                    } label: {
                        Image(systemName: "bolt")
                    }
                    .buttonStyle(IconButtonStyle(size: 20, showsRestingFill: false))
                    .help(self.testLatencyLabel)
                    .accessibilityLabel(self.testLatencyLabel)
                }
            }
            .frame(width: 20, height: 20)
        }
    }

    var rowBackground: Color {
        if self.selected {
            return self.nativeAccent.opacity(T.Opacity.tint)
        }
        if self.isHovered {
            return InteractiveFill.neutral(hovered: true, pressed: false)
        }
        return .clear
    }

    @ViewBuilder
    var delayMetricView: some View {
        if self.delayValue != nil {
            self.delayBadge(
                samples: self.delaySamples,
                fallbackText: self.delayText,
                showsHistory: self.showLatencyHistory)
        } else {
            Text(self.delayText)
                .font(.app(size: T.FontSize.caption, weight: .regular))
                .foregroundStyle(self.delayColor)
                .lineLimit(1)
                .minimumScaleFactor(T.minimumScale)
        }
    }
}

extension View {
    fileprivate func delayBadge(samples: [Int], fallbackText: String, showsHistory: Bool) -> some View {
        let value = samples.last ?? 0
        return HStack(alignment: .center, spacing: 5) {
            LatencySignalBars(samples: samples, showsHistory: showsHistory)
            Text(value == 0 ? fallbackText : "\(value)")
                .font(.system(size: T.FontSize.caption, weight: .medium, design: .monospaced))
                .foregroundStyle(self.nativePrimaryLabel)
                .lineLimit(1)
        }
    }
}

private struct LatencySignalBars: View {
    let samples: [Int]
    let showsHistory: Bool

    private static let barCount = ProxyDelayHistory.limit
    private static let barWidth: CGFloat = 2
    private static let barSpacing: CGFloat = 1.5
    private static let heights: [CGFloat] = [4, 7, 10, 13]

    private var firstVisibleIndex: Int {
        self.showsHistory ? 0 : Self.barCount - 1
    }

    var body: some View {
        HStack(alignment: .bottom, spacing: Self.barSpacing) {
            ForEach(self.firstVisibleIndex..<Self.barCount, id: \.self) { index in
                RoundedRectangle(cornerRadius: 0.75, style: .continuous)
                    .fill(self.barColor(at: index))
                    .frame(width: Self.barWidth, height: Self.heights[index])
            }
        }
        .frame(height: Self.heights.last, alignment: .bottom)
        .accessibilityHidden(true)
    }

    private func barColor(at index: Int) -> Color {
        let offset = Self.barCount - self.samples.count
        let sampleIndex = index - offset
        guard sampleIndex >= 0, sampleIndex < self.samples.count else {
            return Color(nsColor: .quaternaryLabelColor).opacity(0.35)
        }
        return self.latencyColor(self.samples[sampleIndex])
    }
}

