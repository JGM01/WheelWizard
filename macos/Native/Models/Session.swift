import AppKit
import SwiftUI

@MainActor
final class Session: ObservableObject {
    @Published var setup = SetupInput()
    @Published var products = [
        Product(id: "base", name: "Mario Kart Wii", ready: false, detail: "Build required"),
        Product(id: "retro-rewind", name: "Retro Rewind", ready: false, detail: "Build required"),
    ]
    @Published var settings = RuntimeSettings()
    @Published var package = PackageInfo()
    @Published var preflightErrors = [String]()
    @Published var busy = false
    @Published var connected = false

    let activity = Activity()

    var quitWhenIdle = false

    private var process: Process?
    private var input: FileHandle?
    private var pending = [String: String]()
    private var generation = UUID()

    let root = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/WheelWizardNative")
    var preferences: URL { root.appendingPathComponent("preferences.json") }

    init() {
        if let data = try? Data(contentsOf: preferences),
           let value = try? JSONDecoder().decode(SetupInput.self, from: data) {
            setup = value
        }
        connect()
    }

    func connect() {
        guard process == nil else { return }
        generation = UUID()
        let expected = generation
        let task = Process()
        let stdin = Pipe()
        let stdout = Pipe()
        let stderr = Pipe()
        guard let resources = Bundle.main.resourceURL else {
            activity.message = "Missing bundle resources"
            return
        }
        task.executableURL = resources.appendingPathComponent("helper/WheelWizard.Host")
        task.standardInput = stdin
        task.standardOutput = stdout
        task.standardError = stderr
        task.terminationHandler = { [weak self] task in
            Task { @MainActor in
                guard let self, self.process === task else { return }
                self.lost("Helper exited (\(task.terminationStatus)). Reconnect to continue.")
            }
        }
        do {
            try task.run()
            process = task
            input = stdin.fileHandleForWriting
            connected = true
            readLines(stdout.fileHandleForReading, protocolOutput: true, generation: expected)
            readLines(stderr.fileHandleForReading, protocolOutput: false, generation: expected)
            send("preflight")
        } catch {
            lost(error.localizedDescription)
        }
    }

    func readLines(_ handle: FileHandle, protocolOutput: Bool, generation: UUID) {
        DispatchQueue.global().async { [weak self] in
            var buffer = Data()
            while true {
                let chunk = handle.availableData
                if chunk.isEmpty {
                    if protocolOutput {
                        Task { @MainActor in
                            guard let self, self.generation == generation else { return }
                            self.lost("Helper connection closed. Reconnect to continue.")
                        }
                    }
                    break
                }
                buffer.append(chunk)
                var lines = [String]()
                while let end = buffer.firstIndex(of: 10) {
                    lines.append(String(decoding: buffer.prefix(upTo: end), as: UTF8.self))
                    buffer.removeSubrange(...end)
                }
                if !lines.isEmpty {
                    let batch = lines
                    let consumed = DispatchSemaphore(value: 0)
                    Task { @MainActor in
                        defer { consumed.signal() }
                        guard let self, self.generation == generation else { return }
                        for text in batch {
                            guard self.generation == generation else { break }
                            if protocolOutput {
                                self.receive(text)
                            } else {
                                self.activity.append(text)
                            }
                        }
                    }
                    consumed.wait()
                }
                if buffer.count > 4_000_000 {
                    Task { @MainActor in
                        guard let self, self.generation == generation else { return }
                        self.lost("Helper output exceeded the protocol limit")
                    }
                    break
                }
            }
        }
    }

    func lost(_ reason: String) {
        generation = UUID()
        connected = false
        busy = false
        pending.removeAll()
        input = nil
        if let task = process, task.isRunning {
            task.terminate()
        }
        process = nil
        activity.message = reason
        for i in products.indices {
            products[i].ready = false
        }
        if quitWhenIdle {
            NSApp.reply(toApplicationShouldTerminate: true)
        }
    }

    func close() {
        try? input?.close()
    }

    func persist() {
        do {
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            try JSONEncoder().encode(setup).write(to: preferences, options: .atomic)
        } catch {
            activity.message = "Could not save setup: \(error.localizedDescription)"
        }
    }

    func pathBinding(for key: WritableKeyPath<SetupInput, String>) -> Binding<String> {
        Binding(
            get: { self.setup[keyPath: key] },
            set: { value in
                self.setup[keyPath: key] = value
                self.persist()
                self.resetReadiness()
            }
        )
    }

    func choosePath(for key: WritableKeyPath<SetupInput, String>, directory: Bool) {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = directory
        panel.canChooseFiles = !directory
        if panel.runModal() == .OK, let url = panel.url {
            setup[keyPath: key] = url.path
            persist()
            resetReadiness()
        }
    }

    func resetReadiness() {
        for i in products.indices {
            products[i].ready = false
        }
    }

    func reveal(_ runtime: Bool = false) {
        let url = root.appendingPathComponent(runtime ? "Runtime/UserData/Logs" : "Logs")
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        NSWorkspace.shared.open(url)
    }

    func revealLogs() { reveal() }
    func revealRuntimeLogs() { reveal(true) }

    func checkPrerequisites() {
        persist()
        send("preflight")
    }

    func installRR() {
        send("install")
    }

    func build(product id: String) {
        send("build", product: id)
    }

    func launch(product id: String) {
        send("launch", product: id)
    }

    func cancelOperation() {
        send("cancel")
    }

    func saveSettings(volume: Double, resolutionMultiplier: Double) {
        settings.volume = volume
        settings.resolutionMultiplier = resolutionMultiplier
        send("config-write")
    }

    func send(_ command: String, product: String? = nil) {
        guard connected, let input, (!busy || command == "cancel") else { return }
        let id = UUID().uuidString
        do {
            var request: [String: Any] = [
                "version": 1,
                "id": id,
                "command": command,
                "setup": try JSONSerialization.jsonObject(with: JSONEncoder().encode(setup)),
            ]
            if let product {
                request["product"] = product
            }
            if command == "config-write" {
                request["settings"] = try JSONSerialization.jsonObject(with: JSONEncoder().encode(settings))
            }
            var data = try JSONSerialization.data(withJSONObject: request)
            data.append(10)
            pending[id] = command
            if command != "cancel" {
                activity.progress = nil
                busy = true
                if command != "config-read" && command != "status" {
                    activity.message = command == "launch" ? "Game running" : "Working…"
                }
            }
            try input.write(contentsOf: data)
        } catch {
            lost(error.localizedDescription)
        }
    }

    func receive(_ line: String) {
        guard let data = line.data(using: .utf8),
              let event = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              event["version"] as? Int == 1,
              let id = event["id"] as? String,
              let command = pending[id],
              let kind = event["kind"] as? String
        else {
            lost("Invalid helper protocol. Reconnect to continue.")
            return
        }
        let payload = event["data"] as? [String: Any] ?? [:]
        if kind == "log" || kind == "progress" {
            if kind == "progress" {
                activity.progress = payload["percent"] as? Double
            }
            if let text = payload["stage"] as? String {
                activity.append(text)
                if kind == "progress" {
                    activity.message = text
                }
            }
            return
        }
        guard kind == "result" else { return }
        pending.removeValue(forKey: id)
        if command == "cancel" { return }
        busy = false
        let outcome = event["outcome"] as? String ?? "failure"
        if outcome != "success" {
            activity.message = outcome == "cancelled" ? "Cancelled" : event["error"] as? String ?? "Operation failed"
            activity.append(activity.message)
            if command == "launch" {
                send("config-read")
            }
        } else {
            if command != "config-read" && command != "status" {
                activity.message = "Ready"
            }
            if let array = payload["products"],
               let encoded = try? JSONSerialization.data(withJSONObject: array),
               let decoded = try? JSONDecoder().decode([Product].self, from: encoded) {
                products = decoded
            }
            if let package = payload["package"] as? [String: Any] {
                updatePackage(package)
            }
            switch command {
            case "preflight":
                if let value = payload["setup"],
                   let data = try? JSONSerialization.data(withJSONObject: value),
                   let decoded = try? JSONDecoder().decode(SetupInput.self, from: data) {
                    setup = decoded
                    persist()
                }
                preflightErrors = payload["errors"] as? [String] ?? []
                if !preflightErrors.isEmpty {
                    activity.append(preflightErrors.joined(separator: "\n"))
                    activity.message = "Setup needs attention"
                }
                send("status")
            case "status":
                send("config-read")
            case "install":
                updatePackage(payload)
                send("status")
            case "config-read", "config-write":
                if let data = try? JSONSerialization.data(withJSONObject: payload),
                   let decoded = try? JSONDecoder().decode(RuntimeSettings.self, from: data) {
                    settings = decoded
                }
            case "launch":
                send("config-read")
            default:
                break
            }
        }
        if quitWhenIdle && !busy {
            try? input?.close()
            NSApp.reply(toApplicationShouldTerminate: true)
        }
    }

    func updatePackage(_ value: [String: Any]) {
        package.installed = value["installed"] as? Bool ?? false
        package.version = value["version"] as? String ?? ""
    }
}
