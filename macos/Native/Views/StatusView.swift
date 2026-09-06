import SwiftUI

/// A slim status bar pinned to the bottom of the detail pane, modeled on
/// Xcode's activity viewer: a spinner/progress indicator, a single line of
/// status text, and — when relevant — one clear action.
///
/// Unlike the original, this bar is *always* mounted at a fixed height. The
/// previous version collapsed to an empty view when idle and connected,
/// which meant every busy → idle → busy transition resized the safe-area
/// inset and visibly shoved the whole detail pane up and down. Keeping the
/// height constant and cross-fading the content instead removes that jump
/// entirely — the window never "redraws ugly."
struct StatusView: View {
    @EnvironmentObject var session: Session
    @EnvironmentObject var activity: Activity

    private enum Kind: Equatable { case idle, busy, disconnected }
    private var kind: Kind {
        if !session.connected { return .disconnected }
        if session.busy { return .busy }
        return .idle
    }

    var body: some View {
        HStack(spacing: 10) {
            switch kind {
            case .busy: busyContent
            case .disconnected: disconnectedContent
            case .idle: idleContent
            }
        }
        .padding(.horizontal, 14)
        .frame(height: Metrics.statusBarHeight)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.bar)
        .overlay(alignment: .top) { Divider() }
        .animation(.easeInOut(duration: 0.18), value: kind)
    }

    @ViewBuilder
    private var busyContent: some View {
        if let progress = activity.progress {
            ProgressView(value: progress, total: 100)
                .frame(width: 120)
            Text("\(Int(progress))%")
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
        } else {
            ProgressView()
                .controlSize(.small)
        }

        Text(activity.message)
            .font(.caption)
            .lineLimit(1)
            .truncationMode(.tail)

        Spacer(minLength: 8)

        Button {
            session.cancelOperation()
        } label: {
            Label("Stop", systemImage: "stop.circle.fill")
        }
        .disabled(session.stopping)
        .buttonStyle(.borderless)
        .controlSize(.small)
        .foregroundStyle(.red)
    }

    @ViewBuilder
    private var disconnectedContent: some View {
        Image(systemName: "bolt.horizontal.circle.fill")
            .foregroundStyle(.orange)
        Text("Helper not connected")
            .font(.caption)
            .foregroundStyle(.secondary)
        Spacer(minLength: 8)
        Button("Reconnect") { session.connect() }
            .buttonStyle(.bordered)
            .controlSize(.small)
    }

    @ViewBuilder
    private var idleContent: some View {
        StatusDot(on: true)
        Text("Ready")
            .font(.caption)
            .foregroundStyle(.secondary)
        Spacer(minLength: 8)
    }
}
