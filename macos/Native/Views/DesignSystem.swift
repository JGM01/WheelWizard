import SwiftUI

// A small, shared vocabulary so every screen in WheelWizard looks like it
// came from the same app. Nothing here talks to `Session` — it's pure
// presentation, which is what makes it safe to reuse everywhere.

enum Metrics {
    static let cardPadding: CGFloat = 16
    static let cardRadius: CGFloat = 10
    static let sectionSpacing: CGFloat = 22
    /// Fixed height for the bottom status bar. Keeping this constant — and
    /// keeping the bar mounted at all times — is what stops the window from
    /// visibly resizing/jumping when the app goes busy or a helper drops.
    static let statusBarHeight: CGFloat = 34
}

/// A quiet, grouped container for a cluster of related controls — the
/// "settings pane inside a page" look macOS apps use instead of raw stacks
/// of buttons.
struct Card<Content: View>: View {
    /// `true` (default) draws a filled background — use for the main,
    /// required content. `false` draws a thin outline instead — use for
    /// optional/advanced content that should visually read as lower
    /// priority without a second component to maintain.
    var filled: Bool = true
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            content
        }
        .padding(Metrics.cardPadding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background {
            if filled {
                RoundedRectangle(cornerRadius: Metrics.cardRadius, style: .continuous)
                    .fill(.quaternary.opacity(0.25))
            }
        }
        .overlay {
            if !filled {
                RoundedRectangle(cornerRadius: Metrics.cardRadius, style: .continuous)
                    .strokeBorder(Color.secondary.opacity(0.25))
            }
        }
    }
}

/// The one and only way this app reports "something you should know about."
/// Every failure, warning, or recoverable condition renders through this
/// type so the person always gets the same shape: an icon that says how
/// serious it is, a plain-language reason, and — when there's something to
/// *do* about it — a button right there. No dead-end red text.
struct Callout: View {
    enum Kind {
        case info, warning, error, success

        var tint: Color {
            switch self {
            case .info: return .blue
            case .warning: return .orange
            case .error: return .red
            case .success: return .green
            }
        }

        var icon: String {
            switch self {
            case .info: return "info.circle.fill"
            case .warning: return "exclamationmark.triangle.fill"
            case .error: return "xmark.octagon.fill"
            case .success: return "checkmark.circle.fill"
            }
        }
    }

    let kind: Kind
    let title: String
    var message: String? = nil
    var actionTitle: String? = nil
    var action: (() -> Void)? = nil

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: kind.icon)
                .foregroundStyle(kind.tint)
                .imageScale(.medium)
                .padding(.top, 1)

            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(.callout.weight(.semibold))
                if let message, !message.isEmpty {
                    Text(message)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                }
            }

            Spacer(minLength: 8)

            if let actionTitle, let action {
                Button(actionTitle, action: action)
                    .buttonStyle(.bordered)
                    .controlSize(.small)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(kind.tint.opacity(0.12), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
    }
}

/// A single readiness indicator, shared by the sidebar rows and the product
/// header so "ready" always looks like the same green dot everywhere.
struct StatusDot: View {
    var on: Bool
    var color: Color = .green
    var body: some View {
        Circle()
            .fill(on ? color : Color.secondary.opacity(0.3))
            .frame(width: 6, height: 6)
    }
}
