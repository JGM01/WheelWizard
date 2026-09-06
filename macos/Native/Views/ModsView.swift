import SwiftUI
import AppKit

struct ModsView: View {
    @EnvironmentObject var session: Session
    @State private var archivePath = ""
    @State private var importName = ""
    @State private var showImport = false
    @State private var removal: ManagedMod?
    @State private var showAllFiles = false
    private var disabled: Bool { session.busy || !session.connected }
    private var previewFiles: [ModLaunchFile] {
        (session.modPreview ?? []).filter { showAllFiles || !$0.overwritten.isEmpty }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Manage mods and preview which files take precedence. Native Play does not apply these mods yet.")
                    .foregroundStyle(.secondary)
                HStack {
                    Button("Import Archive…", action: chooseArchive)
                    Button("Refresh") { session.refreshMods() }
                    Spacer()
                    Button("Preview Conflicts") { session.modCommand("mods-preview") }
                        .disabled(session.mods.isEmpty)
                }
                .disabled(disabled)

                if !session.resultText(for: "mods").isEmpty {
                    Text(session.resultText(for: "mods"))
                        .foregroundStyle(.red)
                        .textSelection(.enabled)
                }
                if session.busy {
                    ProgressView("Working…")
                }
                if session.modsLoaded && session.mods.isEmpty {
                    ContentUnavailableView("No Mods", systemImage: "shippingbox", description: Text("Import a ZIP, 7z, or RAR archive to start a library."))
                }
                ForEach(Array(session.mods.enumerated()), id: \.element.id) { index, mod in
                    HStack {
                        Toggle(mod.title, isOn: Binding(get: { mod.isEnabled }, set: { enabled in
                            session.modCommand("mods-enabled", fields: ["modTitle": mod.title, "enabled": enabled])
                        }))
                        Spacer()
                        Text("Priority \(mod.priority)").foregroundStyle(.secondary)
                        Button { session.modCommand("mods-move", fields: ["modTitle": mod.title, "direction": -1]) } label: {
                            Image(systemName: "arrow.up")
                        }
                        .help("Move up: higher precedence")
                        .disabled(index == 0)
                        Button { session.modCommand("mods-move", fields: ["modTitle": mod.title, "direction": 1]) } label: {
                            Image(systemName: "arrow.down")
                        }
                        .help("Move down: lower precedence")
                        .disabled(index == session.mods.count - 1)
                        Button("Remove…", role: .destructive) { removal = mod }
                    }
                    .disabled(disabled)
                }
                Divider()
                HStack {
                    Text("File Preview").font(.headline)
                    Spacer()
                    Toggle("All Files", isOn: $showAllFiles).toggleStyle(.switch)
                }
                Text("Earlier mods win overlapping destination files. Tagged archives stay separate; conflicts inside archives are not checked.")
                    .font(.caption).foregroundStyle(.secondary)
                if session.modPreview == nil {
                    Text("Choose Preview Conflicts to scan the saved library.").foregroundStyle(.secondary)
                } else if !session.mods.contains(where: { $0.isEnabled }) {
                    Text("No mods are enabled.").foregroundStyle(.secondary)
                } else if previewFiles.isEmpty {
                    Text(showAllFiles ? "No launch files found." : "No file-level conflicts found.").foregroundStyle(.secondary)
                }
                ForEach(previewFiles) { file in
                    DisclosureGroup {
                        VStack(alignment: .leading, spacing: 6) {
                            Text("Winner: \(file.winner.modTitle)\n\(file.winner.sourcePath)")
                            ForEach(Array(file.overwritten.enumerated()), id: \.offset) { _, source in
                                Text("Overwritten: \(source.modTitle)\n\(source.sourcePath)")
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .font(.caption).textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    } label: {
                        HStack {
                            Text(file.destination)
                            Spacer()
                            Text(file.winner.modTitle).foregroundStyle(.secondary)
                        }
                    }
                }
            }
            .padding(24)
        }
        .navigationTitle("Mods")
        .onAppear { session.refreshMods() }
        .sheet(isPresented: $showImport) {
            VStack(alignment: .leading, spacing: 16) {
                Text("Import Mod").font(.headline)
                Text(URL(fileURLWithPath: archivePath).lastPathComponent).foregroundStyle(.secondary)
                TextField("Mod name", text: $importName)
                HStack {
                    Button("Cancel") { showImport = false }
                    Spacer()
                    Button("Import") {
                        session.modCommand("mods-import", fields: ["archivePath": archivePath, "modTitle": importName.trimmingCharacters(in: .whitespacesAndNewlines)])
                        showImport = false
                    }
                    .disabled(disabled || importName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    .keyboardShortcut(.defaultAction)
                }
            }
            .padding(24).frame(width: 400)
        }
        .alert("Remove Mod?", isPresented: Binding(get: { removal != nil }, set: { if !$0 { removal = nil } })) {
            Button("Remove", role: .destructive) {
                if let mod = removal { session.modCommand("mods-remove", fields: ["modTitle": mod.title]) }
                removal = nil
            }
            Button("Cancel", role: .cancel) { removal = nil }
        } message: {
            Text("Delete \(removal?.title ?? "this mod") from the managed library? The original archive is kept.")
        }
    }

    private func chooseArchive() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.message = "Choose a ZIP, 7z, or RAR mod archive."
        if panel.runModal() == .OK, let url = panel.url {
            archivePath = url.path
            importName = url.deletingPathExtension().lastPathComponent
            showImport = true
        }
    }
}
