import SwiftUI

struct StatusView: View {
    @EnvironmentObject var session: Session
    @EnvironmentObject var activity: Activity

    var body: some View {
        VStack(alignment: .leading) {
            if session.busy {
                HStack {
                    if let progress = activity.progress {
                        ProgressView(value: progress, total: 100)
                    } else {
                        ProgressView()
                    }
                    Text(activity.message).lineLimit(3)
                    Button("Cancel / Stop") { session.cancelOperation() }
                }
            } else {
                Text(activity.message)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
            }
            HStack {
                Button("Reveal Logs") { session.revealLogs() }
                Button("Runtime Logs") { session.revealRuntimeLogs() }
                if !session.connected {
                    Button("Reconnect Helper") { session.connect() }
                }
            }
            DisclosureGroup("Activity Log") {
                ScrollView {
                    Text(activity.log.joined(separator: "\n"))
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 200)
            }
        }
    }
}
