import SwiftUI
import AppKit

struct SetupInput: Codable {
    var wbfs = "", workspace = "", cmake = "", ninja = "", nodtool = "", translator = ""
}
struct Product: Identifiable, Codable {
    let id: String
    let name: String
    var ready: Bool
    var detail: String
}
struct RuntimeSettings: Codable { var volume = 1.0; var resolutionMultiplier = 1.0 }

@MainActor final class AppState: ObservableObject {
    @Published var setup = SetupInput()
    @Published var products = [Product(id: "base", name: "Mario Kart Wii", ready: false, detail: "Build required"), Product(id: "retro-rewind", name: "Retro Rewind", ready: false, detail: "Build required")]
    @Published var settings = RuntimeSettings()
    @Published var selected: String? = "base"
    @Published var progress: Double?
    @Published var busy = false
    @Published var connected = false
    @Published var message = ""
    @Published var log = [String]()
    @Published var preflightErrors = [String]()
    @Published var packageInstalled = false
    @Published var packageVersion = ""
    var process: Process?
    var input: FileHandle?
    var pending = [String: String]()
    var quitWhenIdle = false
    var session = UUID()
    let root = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/WheelWizardNative")
    var preferences: URL { root.appendingPathComponent("preferences.json") }

    init() {
        if let data = try? Data(contentsOf: preferences), let value = try? JSONDecoder().decode(SetupInput.self, from: data) { setup = value }
        connect()
    }
    func append(_ text: String) {
        log.append(text)
        if log.count > 500 { log.removeFirst(log.count - 500) }
    }
    func connect() {
        guard process == nil else { return }
        session = UUID()
        let generation = session
        let task = Process(), stdin = Pipe(), stdout = Pipe(), stderr = Pipe()
        guard let resources = Bundle.main.resourceURL else { message = "Missing bundle resources"; return }
        task.executableURL = resources.appendingPathComponent("helper/WheelWizard.Host")
        task.standardInput = stdin; task.standardOutput = stdout; task.standardError = stderr
        task.terminationHandler = { [weak self] task in
            Task { @MainActor in
                guard let self, self.process === task else { return }
                self.lost("Helper exited (\(task.terminationStatus)). Reconnect to continue.")
            }
        }
        do {
            try task.run(); process = task; input = stdin.fileHandleForWriting; connected = true
            readLines(stdout.fileHandleForReading, protocolOutput: true, generation: generation)
            readLines(stderr.fileHandleForReading, protocolOutput: false, generation: generation)
            send("preflight")
        } catch { lost(error.localizedDescription) }
    }
    func readLines(_ handle: FileHandle, protocolOutput: Bool, generation: UUID) {
        // Dedicated readers keep both pipes draining while the main actor renders.
        DispatchQueue.global().async { [weak self] in
            var buffer = Data()
            while true {
                let chunk = handle.availableData
                if chunk.isEmpty {
                    if protocolOutput { Task { @MainActor in
                        guard let self, self.session == generation else { return }
                        self.lost("Helper connection closed. Reconnect to continue.")
                    } }
                    break
                }
                buffer.append(chunk)
                var lines = [String]()
                while let end = buffer.firstIndex(of: 10) {
                    lines.append(String(decoding: buffer.prefix(upTo: end), as: UTF8.self))
                    buffer.removeSubrange(...end)
                }
                // Bound queued UI work as well as the visible tail. Each pipe has one batch in flight.
                if !lines.isEmpty {
                    let batch = lines
                    let consumed = DispatchSemaphore(value: 0)
                    Task { @MainActor in
                        defer { consumed.signal() }
                        guard let self, self.session == generation else { return }
                        for text in batch {
                            guard self.session == generation else { break }
                            if protocolOutput { self.receive(text) } else { self.append(text) }
                        }
                    }
                    consumed.wait()
                }
                if buffer.count > 4_000_000 {
                    Task { @MainActor in
                        guard let self, self.session == generation else { return }
                        self.lost("Helper output exceeded the protocol limit")
                    }
                    break
                }
            }
        }
    }
    func lost(_ reason: String) {
        session = UUID()
        connected = false; busy = false; pending.removeAll(); input = nil
        if let task = process, task.isRunning { task.terminate() }
        process = nil; message = reason
        for i in products.indices { products[i].ready = false }
        if quitWhenIdle { NSApp.reply(toApplicationShouldTerminate: true) }
    }
    func persist() {
        do { try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true); try JSONEncoder().encode(setup).write(to: preferences, options: .atomic) }
        catch { message = "Could not save setup: \(error.localizedDescription)" }
    }
    func send(_ command: String, product: String? = nil) {
        guard connected, let input, (!busy || command == "cancel") else { return }
        let id = UUID().uuidString
        do {
            var request: [String: Any] = ["version": 1, "id": id, "command": command, "setup": try JSONSerialization.jsonObject(with: JSONEncoder().encode(setup))]
            if let product { request["product"] = product }
            if command == "config-write" { request["settings"] = try JSONSerialization.jsonObject(with: JSONEncoder().encode(settings)) }
            var data = try JSONSerialization.data(withJSONObject: request); data.append(10)
            pending[id] = command
            if command != "cancel" { progress = nil; busy = true; if command != "config-read" && command != "status" { message = command == "launch" ? "Game running" : "Working…" } }
            try input.write(contentsOf: data)
        } catch { lost(error.localizedDescription) }
    }
    func receive(_ line: String) {
        guard let data = line.data(using: .utf8), let event = try? JSONSerialization.jsonObject(with: data) as? [String: Any], event["version"] as? Int == 1,
              let id = event["id"] as? String, let command = pending[id], let kind = event["kind"] as? String else {
            lost("Invalid helper protocol. Reconnect to continue."); return
        }
        let payload = event["data"] as? [String: Any] ?? [:]
        if kind == "log" || kind == "progress" {
            if kind == "progress" { progress = payload["percent"] as? Double }
            if let text = payload["stage"] as? String { append(text); if kind == "progress" { message = text } }
            return
        }
        guard kind == "result" else { return }
        pending.removeValue(forKey: id)
        if command == "cancel" { return }
        busy = false
        let outcome = event["outcome"] as? String ?? "failure"
        if outcome != "success" {
            message = outcome == "cancelled" ? "Cancelled" : event["error"] as? String ?? "Operation failed"
            append(message)
            if command == "launch" { send("config-read") }
        } else {
            if command != "config-read" && command != "status" { message = "Ready" }
            if let array = payload["products"], let encoded = try? JSONSerialization.data(withJSONObject: array), let decoded = try? JSONDecoder().decode([Product].self, from: encoded) { products = decoded }
            if let package = payload["package"] as? [String: Any] { updatePackage(package) }
            switch command {
            case "preflight":
                if let value = payload["setup"], let data = try? JSONSerialization.data(withJSONObject: value), let decoded = try? JSONDecoder().decode(SetupInput.self, from: data) { setup = decoded; persist() }
                preflightErrors = payload["errors"] as? [String] ?? []
                if !preflightErrors.isEmpty { append(preflightErrors.joined(separator: "\n")); message = "Setup needs attention" }
                send("status")
            case "status": send("config-read")
            case "install": updatePackage(payload); send("status")
            case "config-read", "config-write":
                if let data = try? JSONSerialization.data(withJSONObject: payload), let decoded = try? JSONDecoder().decode(RuntimeSettings.self, from: data) { settings = decoded }
            case "launch": send("config-read")
            default: break
            }
        }
        if quitWhenIdle && !busy { try? input?.close(); NSApp.reply(toApplicationShouldTerminate: true) }
    }
    func updatePackage(_ value: [String: Any]) { packageInstalled = value["installed"] as? Bool ?? false; packageVersion = value["version"] as? String ?? "" }
    func pick(_ key: WritableKeyPath<SetupInput, String>, directory: Bool) {
        let panel = NSOpenPanel(); panel.canChooseDirectories = directory; panel.canChooseFiles = !directory
        if panel.runModal() == .OK, let url = panel.url { setup[keyPath: key] = url.path; persist(); for i in products.indices { products[i].ready = false } }
    }
    func reveal(_ runtime: Bool = false) {
        let url = root.appendingPathComponent(runtime ? "Runtime/UserData/Logs" : "Logs")
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        NSWorkspace.shared.open(url)
    }
}

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate {
    weak var state: AppState?
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let state else { return .terminateNow }
        if state.busy {
            let alert = NSAlert(); alert.messageText = "Stop the active operation and quit?"; alert.informativeText = "The helper will stop its build or game process and finish any publication rollback before quitting."
            alert.addButton(withTitle: "Keep Running"); alert.addButton(withTitle: "Stop and Quit")
            guard alert.runModal() == .alertSecondButtonReturn else { return .terminateCancel }
            state.quitWhenIdle = true; state.send("cancel"); return .terminateLater
        }
        try? state.input?.close()
        return .terminateNow
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

@main struct WheelWizardNativeApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject var state = AppState()
    var body: some Scene {
        WindowGroup("WheelWizard") {
            ContentView().environmentObject(state).frame(minWidth: 760, minHeight: 520)
                .onAppear { delegate.state = state }
        }
        Settings { SettingsView().environmentObject(state).padding().frame(width: 400) }
    }
}
struct ContentView: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        NavigationSplitView {
            List(selection: $state.selected) {
                ForEach(state.products) { product in Label(product.name, systemImage: "flag.checkered").tag(product.id) }
                Label("Setup", systemImage: "gearshape").tag("setup")
            }.navigationTitle("WheelWizard")
        } detail: {
            VStack(alignment: .leading, spacing: 16) {
                if state.selected == "setup" { SetupView() }
                else if let product = state.products.first(where: { $0.id == state.selected }) {
                    Text(product.name).font(.largeTitle)
                    if product.id == "retro-rewind" { Text("Offline build").font(.headline); Text("Package: \(state.packageVersion.isEmpty ? "Not installed" : state.packageVersion)") }
                    Text(product.detail)
                    HStack {
                        if product.id == "retro-rewind" && !state.packageInstalled { Button("Download and Install RR") { state.send("install") } }
                        Button(product.ready ? "Rebuild" : "Build") { state.send("build", product: product.id) }.disabled(product.id == "retro-rewind" && !state.packageInstalled)
                        Button("Play") { state.send("launch", product: product.id) }.disabled(!product.ready)
                        SettingsLink { Text("Settings") }
                    }.disabled(state.busy || !state.connected)
                    Text("Use Rebuild after local source edits.").font(.caption).foregroundStyle(.secondary)
                }
                if state.busy { HStack { if let progress = state.progress { ProgressView(value: progress, total: 100).frame(width: 100) } else { ProgressView().controlSize(.small) }; Text(state.message).lineLimit(3); Button("Cancel / Stop") { state.send("cancel") } } }
                else { Text(state.message).foregroundStyle(.secondary).textSelection(.enabled) }
                HStack {
                    Button("Reveal Logs") { state.reveal() }
                    Button("Runtime Logs") { state.reveal(true) }
                    if !state.connected { Button("Reconnect Helper") { state.connect() } }
                }
                DisclosureGroup("Activity Log") {
                    ScrollView { Text(state.log.joined(separator: "\n")).font(.system(.caption, design: .monospaced)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading) }.frame(maxHeight: 220)
                }
                Spacer(minLength: 0)
            }.padding(24)
        }
    }
}
struct SetupView: View {
    @EnvironmentObject var state: AppState
    func picker(_ title: String, _ key: WritableKeyPath<SetupInput, String>, directory: Bool) -> some View {
        HStack { Text(title).frame(width: 110, alignment: .leading); TextField(title, text: Binding(get: { state.setup[keyPath: key] }, set: { state.setup[keyPath: key] = $0; state.persist(); for i in state.products.indices { state.products[i].ready = false } })); Button("Choose…") { state.pick(key, directory: directory) } }
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Setup").font(.largeTitle)
            picker("WBFS", \.wbfs, directory: false)
            picker("WiiCompiled", \.workspace, directory: true)
            DisclosureGroup("Advanced Tool Paths") {
                picker("CMake", \.cmake, directory: false); picker("Ninja", \.ninja, directory: false)
                picker("nodtool", \.nodtool, directory: false); picker("Translator", \.translator, directory: false)
            }
            ForEach(state.preflightErrors, id: \.self) { Text($0).foregroundStyle(.red).font(.caption).textSelection(.enabled) }
            Button("Check Prerequisites") { state.persist(); state.send("preflight") }
            Text("Requires an existing Apple Silicon WiiCompiled checkout and toolchain. Builds use its Assets and generated caches.").font(.caption)
        }.disabled(state.busy || !state.connected)
    }
}
struct SettingsView: View {
    @EnvironmentObject var state: AppState
    var body: some View {
        Form {
            Slider(value: $state.settings.volume, in: 0...1) { Text("Volume") }
            Picker("Resolution", selection: $state.settings.resolutionMultiplier) { ForEach([1.0, 1.5, 2.0, 3.0], id: \.self) { Text("\($0, specifier: "%.1f")×").tag($0) } }
            Button("Save") { state.send("config-write") }
        }.disabled(state.busy || !state.connected)
    }
}
