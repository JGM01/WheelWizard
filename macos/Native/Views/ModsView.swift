import SwiftUI
import AppKit
import UniformTypeIdentifiers

struct ModsView: View {
    @EnvironmentObject var session: Session
    @Environment(\.openWindow) private var openWindow
    @State private var archivePath = ""
    @State private var importName = ""
    @State private var showImport = false
    @State private var removal: ManagedMod?
    @State private var showAllFiles = false
    @State private var draggedTitle: String?
    @State private var draggedIndex: Int?
    @State private var dropIndex: Int?
    private var disabled: Bool { session.busy || !session.connected }
    private let rowPitch: CGFloat = 34
    private var previewFiles: [ModLaunchFile] {
        (session.modPreview ?? []).filter { showAllFiles || !$0.overwritten.isEmpty }
    }

    private var modRows: some View {
        VStack(spacing: 0) {
            ForEach(Array(session.mods.enumerated()), id: \.element.id) { index, mod in
                row(for: mod, index: index)
            }
        }
        .overlay(alignment: .top) { insertionIndicator }
        .onDrop(of: [UTType.text], delegate: ModRowDropDelegate(
            pitch: rowPitch,
            count: { session.mods.count },
            draggedIndex: { draggedIndex },
            setDropIndex: { dropIndex = $0 },
            commit: commitReorder
        ))
    }

    private func row(for mod: ManagedMod, index: Int) -> some View {
        HStack(spacing: 8) {
            grip
                .onDrag {
                    guard !disabled, session.mods.count > 1 else { return NSItemProvider() }
                    draggedTitle = mod.title
                    draggedIndex = index
                    dropIndex = nil
                    return NSItemProvider(object: mod.title as NSString)
                }
            Toggle(mod.title, isOn: Binding(get: { mod.isEnabled }, set: { enabled in
                session.modCommand("mods-enabled", fields: ["modTitle": mod.title, "enabled": enabled])
            }))
            Spacer(minLength: 8)
            Button("Remove…", role: .destructive) { removal = mod }
        }
        .frame(height: rowPitch)
        .disabled(disabled)
    }

    private var grip: some View {
        Image(systemName: "line.3.horizontal")
            .foregroundStyle(.secondary)
            .frame(width: 18, height: rowPitch)
            .contentShape(Rectangle())
            .help("Drag to reorder; top mods take precedence")
    }

    @ViewBuilder
    private var insertionIndicator: some View {
        if let dropIndex, session.mods.count > 1 {
            let maxY = rowPitch * CGFloat(session.mods.count)
            let y = min(max(CGFloat(dropIndex) * rowPitch - 1, 1), maxY - 1)
            Rectangle()
                .fill(Color.accentColor)
                .frame(height: 2)
                .frame(maxWidth: .infinity)
                .offset(y: y)
        }
    }

    private func commitReorder(to finalIndex: Int) -> Bool {
        defer {
            draggedTitle = nil
            draggedIndex = nil
            dropIndex = nil
        }
        guard let title = draggedTitle,
              let source = draggedIndex,
              session.mods.indices.contains(source),
              session.mods[source].title == title
        else { return false }
        var order = session.mods.map(\.title)
        order.remove(at: source)
        order.insert(title, at: min(max(finalIndex, 0), order.count))
        session.reorderMods(order)
        return true
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Manage mods and preview which files take precedence. Native Play does not apply these mods yet.")
                    .foregroundStyle(.secondary)
                HStack {
                    Button("Import Archive…", action: chooseArchive)
                    Button("Browse Mods…") { openWindow(id: "mod-catalog") }
                        .help("Search GameBanana for Mario Kart Wii mods to install")
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
                if !session.mods.isEmpty {
                    modRows
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

// Maps a drag location over the fixed-height mod rows to the gap the row is being dropped into.
private struct ModRowDropDelegate: DropDelegate {
    let pitch: CGFloat
    let count: () -> Int
    let draggedIndex: () -> Int?
    let setDropIndex: (Int?) -> Void
    let commit: (Int) -> Bool

    private func gap(from info: DropInfo) -> Int? {
        let n = count()
        guard n > 1, let source = draggedIndex(), (0..<n).contains(source) else { return nil }
        var index = Int((info.location.y + pitch / 2) / pitch)
        index = min(max(index, 0), n)
        return index
    }

    func dropEntered(info: DropInfo) {
        setDropIndex(gap(from: info))
    }

    func dropUpdated(info: DropInfo) -> DropProposal? {
        setDropIndex(gap(from: info))
        return nil
    }

    func dropExited(info: DropInfo) {
        setDropIndex(nil)
    }

    func performDrop(info: DropInfo) -> Bool {
        guard let source = draggedIndex(), let target = gap(from: info) else {
            setDropIndex(nil)
            return false
        }
        let n = count()
        // Removing the source shifts later indices down by one, so a target past it lands one lower.
        var finalIndex = target > source ? target - 1 : target
        finalIndex = min(max(finalIndex, 0), n - 1)
        setDropIndex(nil)
        return commit(finalIndex)
    }
}
