import SwiftUI

struct SetupView: View {
    @EnvironmentObject var session: Session

    private var locked: Bool { session.busy || !session.connected }
    /// Preflight has run at least once and came back clean — worth saying so,
    /// otherwise a successful check just silently produces... nothing.
    private var verified: Bool { session.dataStates["setup"] == .loaded && session.preflightErrors.isEmpty }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Prerequisites")
                            .font(.title2.bold())
                        Text("Requires an existing Apple Silicon WiiCompiled checkout and toolchain. Builds use its Assets and generated caches.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    if !session.preflightErrors.isEmpty {
                        Callout(kind: .error, title: "Setup Needs Attention", message: session.preflightErrors.joined(separator: "\n"))
                    } else if verified {
                        Callout(kind: .success, title: "All Prerequisites Found")
                    }
                }

                Card {
                    Text("Required").font(.headline)
                    VStack(spacing: 0) {
                        pathRow("WBFS", systemImage: "externaldrive", key: \.wbfs, directory: false)
                        Divider()
                        pathRow("WiiCompiled", systemImage: "folder", key: \.workspace, directory: true)
                    }
                }

                Card(filled: false) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Advanced").font(.headline)
                        Text("Only needed for a custom toolchain — the defaults work for most setups.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    VStack(spacing: 0) {
                        pathRow("CMake", systemImage: "hammer", key: \.cmake, directory: false)
                        Divider()
                        pathRow("Ninja", systemImage: "bolt.fill", key: \.ninja, directory: false)
                        Divider()
                        pathRow("nodtool", systemImage: "terminal", key: \.nodtool, directory: false)
                        Divider()
                        pathRow("Translator", systemImage: "character.bubble", key: \.translator, directory: false)
                    }
                }

                // Deliberately outside any Form/List — a bordered-prominent
                // button dropped into a grouped-list background picks up an
                // ugly inherited fill on macOS. Plain container, plain button.
                HStack {
                    Button {
                        session.checkPrerequisites()
                    } label: {
                        ZStack {
                            Text("Validate").opacity(session.busy ? 0 : 1)
                            if session.busy { ProgressView().controlSize(.small) }
                        }
                        .frame(width: 128)
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .disabled(locked)
                }
            }
            .padding(20)
        }
    }

    private func pathRow(
        _ title: String,
        systemImage: String,
        key: WritableKeyPath<SetupInput, String>,
        directory: Bool
    ) -> some View {
        HStack(spacing: 12) {
            Image(systemName: systemImage)
                .foregroundStyle(.secondary)
                .frame(width: 20)
            Text(title)
                .frame(width: 90, alignment: .leading)
            TextField("", text: session.pathBinding(for: key))
                .textFieldStyle(.roundedBorder)
                .disabled(locked)
            Button("Choose…") { session.choosePath(for: key, directory: directory) }
                .disabled(locked)
        }
        .padding(.vertical, 6)
    }
}
