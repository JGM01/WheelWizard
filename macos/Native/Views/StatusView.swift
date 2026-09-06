import SwiftUI

/// A slim status bar pinned to the bottom of the detail pane, modeled on
/// Xcode's activity viewer: a spinner/progress indicator, a single line of
/// status text, and — when relevant — one clear action. It stays invisible
/// (via `Group` producing an empty body) when there's nothing to report,
/// so it never competes with the content above it.
struct StatusView: View {
    @EnvironmentObject var session: Session
    @EnvironmentObject var activity: Activity

    var body: some View {
        Group {
            if session.busy {
                busyBar
            } else if !session.connected {
                disconnectedBar
            }
        }
    }

    private var busyBar: some View {
        HStack(spacing: 10) {
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
            .buttonStyle(.borderless)
            .controlSize(.small)
            .foregroundStyle(.red)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(.bar)
        .overlay(alignment: .top) { Divider() }
    }

    private var disconnectedBar: some View {
        HStack(spacing: 10) {
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
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(.bar)
        .overlay(alignment: .top) { Divider() }
    }
}
