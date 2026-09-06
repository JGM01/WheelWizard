import SwiftUI

struct SetupView: View {
    @EnvironmentObject var session: Session

    var body: some View {
        VStack(alignment: .leading) {
            Text("Setup").font(.largeTitle)
            Form {
                pathField("WBFS", key: \.wbfs, directory: false)
                pathField("WiiCompiled", key: \.workspace, directory: true)
                DisclosureGroup("Advanced Tool Paths") {
                    pathField("CMake", key: \.cmake, directory: false)
                    pathField("Ninja", key: \.ninja, directory: false)
                    pathField("nodtool", key: \.nodtool, directory: false)
                    pathField("Translator", key: \.translator, directory: false)
                }
                ForEach(session.preflightErrors, id: \.self) { error in
                    Text(error)
                        .font(.caption)
                        .foregroundStyle(.red)
                        .textSelection(.enabled)
                }
                Button("Check Prerequisites") { session.checkPrerequisites() }
                Text("Requires an existing Apple Silicon WiiCompiled checkout and toolchain. Builds use its Assets and generated caches.")
                    .font(.caption)
            }
            .formStyle(.grouped)
        }
        .disabled(session.busy || !session.connected)
    }

    private func pathField(_ title: String, key: WritableKeyPath<SetupInput, String>, directory: Bool) -> some View {
        LabeledContent {
            TextField(title, text: session.pathBinding(for: key))
            Button("Choose…") { session.choosePath(for: key, directory: directory) }
        } label: {
            Text(title)
        }
    }
}
