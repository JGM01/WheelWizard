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
    @Published var domainResults = [String: String]()
    @Published var preflightErrors = [String]()
    @Published var busy = false
    @Published var connected = false

    @Published var mods = [ManagedMod]()
    @Published var modPreview: [ModLaunchFile]?
    @Published var modsLoaded = false
    private var wantsModsRefresh = false

    @Published var catalogResults = [CatalogMod]()
    @Published var catalogDetail: CatalogModDetail?
    @Published var catalogBusy = false
    var catalogComplete = true
    private var catalogQuery = ""
    private var catalogPage = 1
    private var catalogRequestedPage = 1

    let activity = Activity()

    var quitWhenIdle = false

    private var process: Process?
    private var input: FileHandle?
    private var pending = [String: String]()
    private var pendingDomain = [String: String]()
    private var generation = UUID()
    private var wantsPackageState = false
    private var packageStateFeedback = false

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
        pendingDomain.removeAll()
        domainResults.removeAll()
        modsLoaded = false
        modPreview = nil
        wantsModsRefresh = false
        catalogResults.removeAll()
        catalogDetail = nil
        catalogBusy = false
        catalogComplete = true
        catalogQuery = ""
        catalogPage = 1
        catalogRequestedPage = 1
        wantsPackageState = false
        packageStateFeedback = false
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
        send("preflight", domain: "setup")
    }

    func installRR() {
        send("install", domain: "retro-rewind")
    }

    func updateRR() {
        send("package-update", domain: "retro-rewind")
    }

    func removeRR() {
        send("package-remove", domain: "retro-rewind")
    }

    func checkForRRUpdates() {
        requestPackageState(feedback: true)
    }

    private func requestPackageState(feedback: Bool) {
        guard connected, !busy, let input else { return }
        packageStateFeedback = feedback
        let id = UUID().uuidString
        do {
            let request: [String: Any] = [
                "version": 1,
                "id": id,
                "command": "package-state",
                "setup": try JSONSerialization.jsonObject(with: JSONEncoder().encode(setup)),
            ]
            var data = try JSONSerialization.data(withJSONObject: request)
            data.append(10)
            busy = true
            pending[id] = "package-state"
            pendingDomain[id] = "retro-rewind"
            try input.write(contentsOf: data)
        } catch {
            packageStateFeedback = false
            lost(error.localizedDescription)
        }
    }

    func build(product id: String) {
        send("build", product: id, domain: id)
    }

    func launch(product id: String) {
        send("launch", product: id, domain: id)
    }

    func cancelOperation() {
        send("cancel")
    }

    func saveSettings(volume: Double, resolutionMultiplier: Double) {
        settings.volume = volume
        settings.resolutionMultiplier = resolutionMultiplier
        send("config-write")
    }

    func send(_ command: String, product: String? = nil, domain: String? = nil, fields: [String: Any] = [:]) {
        guard connected, let input, (!busy || command == "cancel") else { return }
        let id = UUID().uuidString
        do {
            var request: [String: Any] = [
                "version": 1,
                "id": id,
                "command": command,
                "setup": try JSONSerialization.jsonObject(with: JSONEncoder().encode(setup)),
            ]
            request.merge(fields) { _, supplied in supplied }
            if let product {
                request["product"] = product
            }
            if command == "config-write" {
                request["settings"] = try JSONSerialization.jsonObject(with: JSONEncoder().encode(settings))
            }
            var data = try JSONSerialization.data(withJSONObject: request)
            data.append(10)
            pending[id] = command
            if let domain {
                pendingDomain[id] = domain
            }
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

    func resultText(for domain: String) -> String {
        domainResults[domain] ?? ""
    }

    private func setResult(_ domain: String?, _ text: String) {
        guard let domain, !domain.isEmpty else { return }
        domainResults[domain] = text
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
        let domain = pendingDomain.removeValue(forKey: id) ?? ""
        if command == "cancel" { return }
        busy = false
        let outcome = event["outcome"] as? String ?? "failure"
        if outcome != "success" {
            let text = outcome == "cancelled" ? "Cancelled" : event["error"] as? String ?? "Operation failed"
            activity.message = text
            activity.append(text)
            setResult(domain, text)
            if ["mods-import", "mods-enabled", "mods-move", "mods-reorder", "mods-remove", "mods-install"].contains(command) {
                wantsModsRefresh = true
            }
            if ["mods-search", "mods-details", "mods-install"].contains(command) {
                catalogBusy = false
            }
            if command == "launch" {
                send("config-read")
            }
        } else {
            if command != "config-read" && command != "status" && command != "package-state" {
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
            case "mods-preview":
                do {
                    let data = try JSONSerialization.data(withJSONObject: payload["files"] ?? [])
                    modPreview = try JSONDecoder().decode([ModLaunchFile].self, from: data)
                } catch {
                    setResult("mods", "Could not read mod results: \(error.localizedDescription)")
                }
            case "mods-search":
                catalogBusy = false
                do {
                    let data = try JSONSerialization.data(withJSONObject: payload)
                    let page = try JSONDecoder().decode(CatalogSearchPage.self, from: data)
                    if catalogRequestedPage == 1 {
                        catalogResults = page.results
                    } else {
                        var seen = Set(catalogResults.map(\.id))
                        catalogResults += page.results.filter { seen.insert($0.id).inserted }
                    }
                    catalogComplete = page.isComplete
                    catalogPage = catalogRequestedPage
                    setResult("catalog", "")
                } catch {
                    setResult("catalog", "Could not read catalog results: \(error.localizedDescription)")
                }
            case "mods-details":
                catalogBusy = false
                do {
                    let data = try JSONSerialization.data(withJSONObject: payload)
                    catalogDetail = try JSONDecoder().decode(CatalogModDetail.self, from: data)
                    setResult("catalog", "")
                } catch {
                    setResult("catalog", "Could not read mod details: \(error.localizedDescription)")
                }
            case "mods-list", "mods-import", "mods-enabled", "mods-move", "mods-reorder", "mods-remove", "mods-install":
                do {
                    let data = try JSONSerialization.data(withJSONObject: payload["mods"] ?? [])
                    mods = try JSONDecoder().decode([ManagedMod].self, from: data)
                    modsLoaded = true
                    if command != "mods-list" { setResult("mods", "") }
                } catch {
                    setResult("mods", "Could not read mod results: \(error.localizedDescription)")
                }
                if command == "mods-install" {
                    catalogBusy = false
                    setResult("catalog", "")
                }
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
                }
                setResult(domain, preflightErrors.isEmpty ? "" : "Setup needs attention")
                send("status")
            case "status":
                send("config-read")
            case "install":
                updatePackage(payload)
                let installed = "Retro Rewind installed"
                setResult(domain, installed)
                wantsPackageState = true
                send("status")
            case "package-update":
                updatePackage(payload)
                let updated = package.version.isEmpty
                    ? "Retro Rewind updated — rebuild required"
                    : "Retro Rewind updated to \(package.version) — rebuild required"
                setResult(domain, updated)
                activity.append(updated)
                wantsPackageState = true
                send("status")
            case "package-remove":
                updatePackage(payload)
                let removed = "Removed Retro Rewind"
                setResult(domain, removed)
                activity.append(removed)
                wantsPackageState = true
                send("status")
            case "package-state":
                updatePackageState(payload)
                if packageStateFeedback {
                    packageStateFeedback = false
                    let text: String
                    if !package.serverReachable {
                        text = "Update check failed — couldn't reach the server"
                    } else if !package.installed {
                        text = "Retro Rewind is not installed"
                    } else if package.outOfDate {
                        text = "Update available: \(package.latest)"
                    } else {
                        text = "Retro Rewind is up to date"
                    }
                    setResult(domain, text)
                    activity.append(text)
                }
            case "config-read", "config-write":
                if let data = try? JSONSerialization.data(withJSONObject: payload),
                   let decoded = try? JSONDecoder().decode(RuntimeSettings.self, from: data) {
                    settings = decoded
                }
            case "launch":
                setResult(domain, "")
                send("config-read")
            case "build":
                setResult(domain, "")
            default:
                break
            }
        }
        if wantsModsRefresh && !busy && connected {
            wantsModsRefresh = false
            send("mods-list", domain: "mods")
        }
        if wantsPackageState && !busy {
            wantsPackageState = false
            requestPackageState(feedback: false)
        }
        if quitWhenIdle && !busy {
            try? input?.close()
            NSApp.reply(toApplicationShouldTerminate: true)
        }
    }

    func refreshMods() {
        domainResults["mods"] = ""
        modPreview = nil
        if busy {
            wantsModsRefresh = true
        } else {
            send("mods-list", domain: "mods")
        }
    }

    func modCommand(_ command: String, fields: [String: Any] = [:]) {
        guard connected, !busy else { return }
        modPreview = nil
        domainResults["mods"] = ""
        send(command, domain: "mods", fields: fields)
    }

    func reorderMods(_ orderedTitles: [String]) {
        modCommand("mods-reorder", fields: ["titles": orderedTitles])
    }

    func catalogCommand(_ command: String, fields: [String: Any]) {
        guard connected, !busy else { return }
        catalogBusy = true
        domainResults["catalog"] = ""
        send(command, domain: "catalog", fields: fields)
    }

    // The catalog query text survives window close/reopen, so the search field can be reseeded.
    var activeCatalogQuery: String { catalogQuery }

    // Drop stale detail and error state when the browser reopens; the last results are kept.
    func clearCatalogFeedback() {
        domainResults["catalog"] = ""
        catalogDetail = nil
    }

    // First page of a (possibly empty) search replaces the list; later pages append.
    func reloadCatalog(_ query: String) {
        catalogQuery = query
        catalogRequestedPage = 1
        catalogResults.removeAll()
        catalogComplete = false
        catalogDetail = nil
        catalogCommand("mods-search", fields: ["search": query, "page": 1])
    }

    func loadMoreCatalog() {
        guard !catalogComplete else { return }
        catalogRequestedPage = catalogPage + 1
        catalogCommand("mods-search", fields: ["search": catalogQuery, "page": catalogRequestedPage])
    }

    func loadModDetails(_ id: Int) {
        catalogCommand("mods-details", fields: ["modId": id])
    }

    func installCatalogMod(detail: CatalogModDetail, title: String) {
        let file = detail.files.first ?? detail.archivedFiles.first
        guard let file else { return }
        catalogCommand("mods-install", fields: [
            "url": file.downloadUrl,
            "modTitle": title,
            "author": detail.author.name,
            "modId": detail.id,
        ])
    }

    func updatePackage(_ value: [String: Any]) {
        package.installed = value["installed"] as? Bool ?? false
        package.version = value["version"] as? String ?? ""
    }

    func updatePackageState(_ value: [String: Any]) {
        package.installed = value["installed"] as? Bool ?? package.installed
        package.version = value["version"] as? String ?? package.version
        package.latest = value["latest"] as? String ?? ""
        package.outOfDate = value["outOfDate"] as? Bool ?? false
        package.serverReachable = value["serverReachable"] as? Bool ?? true
    }
}
