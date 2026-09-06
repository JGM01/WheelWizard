import SwiftUI

struct SetupView: View {
    @EnvironmentObject var session: Session

    private var setupResult: String { session.resultText(for: "setup") }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Form {
                Section("Required Paths") {
                    pathField("WBFS", systemImage: "externaldrive", key: \.wbfs, directory: false)
                    pathField("WiiCompiled", systemImage: "folder", key: \.workspace, directory: true)
                }

                Section {
                    DisclosureGroup("Advanced Tool Paths") {
                        pathField("CMake", systemImage: "hammer", key: \.cmake, directory: false)
                        pathField("Ninja", systemImage: "bolt.fill", key: \.ninja, directory: false)
                        pathField("nodtool", systemImage: "terminal", key: \.nodtool, directory: false)
                        pathField("Translator", systemImage: "character.bubble", key: \.translator, directory: false)
                    }
                }

                Section {
                    HStack {
                        Spacer()
                        Button("Check Prerequisites") { session.checkPrerequisites() }
                            .buttonStyle(.borderedProminent)
                    }
                }
            }
            .formStyle(.grouped)

            if !session.preflightErrors.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(session.preflightErrors, id: \.self) { error in
                        Label(error, systemImage: "xmark.octagon.fill")
                            .font(.caption)
                            .foregroundStyle(.red)
                            .textSelection(.enabled)
                    }
                }
                .padding(.horizontal, 20)
            }

            if !setupResult.isEmpty {
                Text(setupResult)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                    .padding(.horizontal, 20)
            }

            Text("Requires an existing Apple Silicon WiiCompiled checkout and toolchain. Builds use its Assets and generated caches.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .padding(.horizontal, 20)
                .padding(.bottom, 12)
        }
        .disabled(session.busy || !session.connected)
    }

    private func pathField(
        _ title: String,
        systemImage: String,
        key: WritableKeyPath<SetupInput, String>,
        directory: Bool
    ) -> some View {
        LabeledContent {
            HStack {
                TextField("", text: session.pathBinding(for: key))
                    .textFieldStyle(.roundedBorder)
                Button("Choose…") { session.choosePath(for: key, directory: directory) }
            }
        } label: {
            Label(title, systemImage: systemImage)
        }
    }
}
