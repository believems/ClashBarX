import AppKit
import SwiftUI

/// Native segmented control whose segments always share the full width equally.
///
/// SwiftUI's `.segmented` picker sizes itself to its labels when linked against the macOS 26 SDK
/// and ignores a wider frame, so the same code renders full-width or compact depending on the SDK
/// it was built with. `NSSegmentedControl.segmentDistribution = .fillEqually` pins the layout.
struct FillSegmentedControl: NSViewRepresentable {
    let labels: [String]
    let selectedIndex: Int?
    var controlSize: NSControl.ControlSize = .large
    let onSelect: (Int) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(onSelect: self.onSelect)
    }

    func makeNSView(context: Context) -> NSSegmentedControl {
        let control = NSSegmentedControl(
            labels: self.labels,
            trackingMode: .selectOne,
            target: context.coordinator,
            action: #selector(Coordinator.changed(_:)))
        control.segmentDistribution = .fillEqually
        control.controlSize = self.controlSize
        control.setContentHuggingPriority(.defaultLow, for: .horizontal)
        control.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        return control
    }

    func updateNSView(_ control: NSSegmentedControl, context: Context) {
        context.coordinator.onSelect = self.onSelect
        context.coordinator.selectedIndex = self.selectedIndex
        if control.segmentCount != self.labels.count {
            control.segmentCount = self.labels.count
        }
        for (index, label) in self.labels.enumerated() where control.label(forSegment: index) != label {
            control.setLabel(label, forSegment: index)
        }
        control.controlSize = self.controlSize
        control.isEnabled = context.environment.isEnabled
        if let selectedIndex, selectedIndex < control.segmentCount {
            if control.selectedSegment != selectedIndex {
                control.selectedSegment = selectedIndex
            }
        } else if control.selectedSegment != -1 {
            control.selectedSegment = -1
        }
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: NSSegmentedControl, context: Context) -> CGSize? {
        let height = nsView.intrinsicContentSize.height
        return CGSize(width: proposal.width ?? nsView.intrinsicContentSize.width, height: height)
    }

    @MainActor
    final class Coordinator: NSObject {
        var onSelect: (Int) -> Void
        /// Latest selection from SwiftUI state; the control is snapped back to it when a click is
        /// rejected (e.g. during an in-flight switch), since no state change would re-render it.
        var selectedIndex: Int?

        init(onSelect: @escaping (Int) -> Void) {
            self.onSelect = onSelect
        }

        @objc func changed(_ sender: NSSegmentedControl) {
            self.onSelect(sender.selectedSegment)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { [weak self, weak sender] in
                guard let self, let sender else { return }
                let expected = self.selectedIndex ?? -1
                if sender.selectedSegment != expected {
                    sender.selectedSegment = expected
                }
            }
        }
    }
}
