import AppKit

/// Hosts a floating panel's content inside AppKit's own Liquid Glass on macOS 26+.
///
/// The glass draws its own soft shadow, so windows using it turn off the system shadow (which adds
/// a dark outline around borderless windows) and leave a transparent `shadowMargin` around the
/// glass; without that margin the window rectangle clips the glass shadow into dark square corners.
/// Callers size the window as content + 2 × margin and place it offset by the margin.
final class PanelGlassContainerController: NSViewController {
    static let shadowMargin: CGFloat = 20

    /// Margin to apply on this system: zero where the SwiftUI material fallback is used.
    static var effectiveShadowMargin: CGFloat {
        if #available(macOS 26.0, *) { return self.shadowMargin }
        return 0
    }

    private let child: NSViewController
    private let cornerRadius: CGFloat

    private init(child: NSViewController, cornerRadius: CGFloat) {
        self.child = child
        self.cornerRadius = cornerRadius
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    /// Wraps `child` in glass on macOS 26+; returns it unchanged on older systems.
    static func wrapping(_ child: NSViewController, cornerRadius: CGFloat) -> NSViewController {
        if #available(macOS 26.0, *) {
            return PanelGlassContainerController(child: child, cornerRadius: cornerRadius)
        }
        return child
    }

    override func loadView() {
        guard #available(macOS 26.0, *) else {
            self.view = NSView()
            return
        }
        let glass = NSGlassEffectView()
        glass.cornerRadius = self.cornerRadius
        glass.style = .regular
        self.addChild(self.child)
        self.child.view.autoresizingMask = [.width, .height]
        glass.contentView = self.child.view

        let side = Self.shadowMargin * 2 + 100
        let container = NSView(frame: NSRect(x: 0, y: 0, width: side, height: side))
        glass.frame = container.bounds.insetBy(dx: Self.shadowMargin, dy: Self.shadowMargin)
        glass.autoresizingMask = [.width, .height]
        container.addSubview(glass)
        self.view = container
    }
}
