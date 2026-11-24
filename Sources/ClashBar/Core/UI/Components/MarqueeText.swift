import SwiftUI

/// Single-line text that truncates at rest and, while `isActive`, slowly scrolls to reveal the
/// part that does not fit. Keeps the row width fixed instead of letting long names grow it.
struct MarqueeText: View {
    let text: String
    var font: Font
    var color: Color
    var truncationMode: Text.TruncationMode = .middle
    var isActive: Bool

    /// Points per second; slow enough to read.
    private let speed: CGFloat = 40
    private let edgePause: Double = 0.6

    @State private var textWidth: CGFloat = 0
    @State private var containerWidth: CGFloat = 0
    @State private var offset: CGFloat = 0

    private var overflow: CGFloat {
        max(0, (self.textWidth - self.containerWidth).rounded(.up))
    }

    private var isScrolling: Bool {
        self.isActive && self.overflow > 1
    }

    var body: some View {
        Text(self.text)
            .font(self.font)
            .foregroundStyle(self.color)
            .lineLimit(1)
            .truncationMode(self.truncationMode)
            .opacity(self.isScrolling ? 0 : 1)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(GeometryReader { proxy in
                Color.clear
                    .onAppear { self.containerWidth = proxy.size.width }
                    .onChange(of: proxy.size.width) { self.containerWidth = $0 }
            })
            .overlay(alignment: .leading) {
                // Measures the full single-line width without affecting layout.
                Text(self.text)
                    .font(self.font)
                    .fixedSize()
                    .hidden()
                    .background(GeometryReader { proxy in
                        Color.clear
                            .onAppear { self.textWidth = proxy.size.width }
                            .onChange(of: proxy.size.width) { self.textWidth = $0 }
                    })
            }
            .overlay(alignment: .leading) {
                if self.isScrolling {
                    Text(self.text)
                        .font(self.font)
                        .foregroundStyle(self.color)
                        .fixedSize()
                        .offset(x: self.offset)
                }
            }
            .clipped()
            .onChange(of: self.isScrolling) { scrolling in
                self.restart(scrolling: scrolling)
            }
            .onChange(of: self.text) { _ in
                self.restart(scrolling: self.isScrolling)
            }
    }

    private func restart(scrolling: Bool) {
        var reset = Transaction(animation: nil)
        reset.disablesAnimations = true
        withTransaction(reset) { self.offset = 0 }

        guard scrolling else { return }
        let duration = Double(self.overflow / self.speed)
        withAnimation(
            .linear(duration: duration)
                .delay(self.edgePause)
                .repeatForever(autoreverses: true))
        {
            self.offset = -self.overflow
        }
    }
}
