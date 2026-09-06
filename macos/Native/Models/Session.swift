import AppKit
import SwiftUI

@MainActor
final class Session: ObservableObject {
    @Published private(set) var setup = SetupInput()
    @Published private(set) var products = [
        Product(id: "base", name: "Mario Kart Wii", ready: false, detail: "Build required"),
        Product(id: "retro-rewind", name: "Retro Rewind", ready: false, detail: "Build required"),
    ]
    @Published private(set) var settings = RuntimeSettings()
    @Published private(set) var package = PackageInfo()
    @Published private(set) var domainResults = [String: String]()
    @Published private(set) var preflightErrors = [String]()
    @Published private(set) var control = SessionControl()
    @Published private(set) var dataStates: [String: LoadState] = [:]
    var busy: Bool { control.state.busy }
    var connected: Bool { control.state.connected }
    var stopping: Bool { control.state.stopping }
    var quitWhenIdle: Bool { control.quitRequested }
    var awaitingPatchChoice: Bool {
        control.state.operation?.phase == .awaitingChoice && !stopping && !controls.values.contains("launch-choice")
    }
    @Published private(set) var completedLaunchCount = 0
    @Published private(set) var recovery: PatchRecovery?
    @Published private(set) var mods = [ManagedMod]()
    @Published private(set) var modPreview: [ModLaunchFile]?
    var modsLoaded: Bool { dataStates["mods"] == .loaded }
    var settingsLoaded: Bool { dataStates["settings"] == .loaded }
    @Published private(set) var catalogResults = [CatalogMod]()
    @Published private(set) var catalogDetail: CatalogModDetail?
    var catalogBusy: Bool { control.state.operation?.context.command.catalog == true }
    @Published private(set) var catalogComplete = true
    private var catalogQuery = ""
    private var catalogPage = 1
    let activity = Activity()
    private var process: Process?
    private var input: FileHandle?
    private var controls: [String: String] = [:]
    private var generation = UUID()

    let root = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/WheelWizardNative")
    var preferences: URL { root.appendingPathComponent("preferences.json") }

    init(transport: FileHandle? = nil) {
        if let transport {
            input = transport
            control.connected()
            return
        }
        if let data = try? Data(contentsOf: preferences),
           let value = try? JSONDecoder().decode(SetupInput.self, from: data) {
            setup = value
        }
        connect()
    }

    func connect() {
        guard process == nil else { return }
        control.connecting()
        generation = UUID()
        let expected = generation
        let task = Process()
        let stdin = Pipe()
        let stdout = Pipe()
        let stderr = Pipe()
        guard let resources = Bundle.main.resourceURL else {
            lost("Missing bundle resources")
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
            control.connected()
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
        control.disconnect(reason)
        controls.removeAll()
        dataStates.removeAll()
        domainResults.removeAll()
        modPreview = nil
        recovery = nil
        catalogResults.removeAll()
        catalogDetail = nil
        catalogComplete = true
        catalogQuery = ""
        catalogPage = 1
        try? input?.close()
        input = nil
        let retiring = process
        if let task = process, task.isRunning {
            task.terminate()
        }
        process = nil
        activity.message = reason
        for i in products.indices {
            products[i].ready = false
        }
        if quitWhenIdle { finishQuit(after: retiring) }
    }

    private func finishQuit(after task: Process?) {
        guard let task, task.isRunning else { NSApp.reply(toApplicationShouldTerminate: true); return }
        DispatchQueue.global().async {
            task.waitUntilExit()
            Task { @MainActor in NSApp.reply(toApplicationShouldTerminate: true) }
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
                guard self.connected, !self.busy else { return }
                self.setup[keyPath: key] = value
                self.persist()
                self.resetReadiness()
            }
        )
    }

    func choosePath(for key: WritableKeyPath<SetupInput, String>, directory: Bool) {
        guard connected, !busy else { return }
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
        dataStates["products"] = .unavailable
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
        send("package-state", domain: "retro-rewind", feedback: feedback)
    }

    func build(product id: String) { send("build", product: id, domain: id) }
    func launch(product id: String) { send("launch", product: id, domain: id) }
    func restorePatches() { send("patches-restore", domain: "retro-rewind") }

    func stopAndQuit() {
        control.stop(quit: true)
        cancelOperation()
    }

    func cancelOperation() {
        guard control.state.operation != nil, !controls.values.contains("cancel") else { return }
        control.stop()
        activity.message = "Stopping…"
        sendControl("cancel")
    }

    func choosePatches(delete: Bool) {
        guard awaitingPatchChoice, let request = control.state.operation?.context else { return }
        guard control.advance(id: request.id, phase: .choiceSubmitted) else { return }
        activity.message = LaunchPhase.choiceSubmitted.label
        sendControl("launch-choice", fields: ["launchId": request.id, "choice": delete ? "delete" : "keep"])
    }

    private func sendControl(_ command: String, fields: [String: Any] = [:]) {
        guard connected, let input else { return }
        let id = UUID().uuidString
        do {
            var request: [String: Any] = ["version": 1, "id": id, "command": command]
            request.merge(fields) { _, value in value }
            var data = try JSONSerialization.data(withJSONObject: request)
            data.append(10)
            controls[id] = command
            try input.write(contentsOf: data)
        } catch { lost(error.localizedDescription) }
    }

    func saveSettings(volume: Double, resolutionMultiplier: Double) {
        let draft = RuntimeSettings(volume: volume, resolutionMultiplier: resolutionMultiplier)
        guard let value = try? JSONSerialization.jsonObject(with: JSONEncoder().encode(draft)) else { return }
        send("config-write", fields: ["settings": value])
    }

    @discardableResult
    func send(_ name: String, product: String? = nil, domain: String? = nil, fields: [String: Any] = [:], feedback: Bool = false) -> Bool {
        guard connected, !busy, !quitWhenIdle, let input, let command = Command(rawValue: name) else { return false }
        let context = RequestContext(id: UUID().uuidString, command: command, domain: domain ?? "", product: product,
                                     query: fields["search"] as? String, page: fields["page"] as? Int, feedback: feedback)
        do {
            var request: [String: Any] = ["version": 1, "id": context.id, "command": name,
                "setup": try JSONSerialization.jsonObject(with: JSONEncoder().encode(setup))]
            request.merge(fields) { _, supplied in supplied }
            if let product { request["product"] = product }
            var data = try JSONSerialization.data(withJSONObject: request)
            data.append(10)
            guard control.start(context) else { return false }
            if let dataDomain = command.dataDomain { dataStates[dataDomain] = .loading }
            activity.progress = nil
            activity.message = command == .launch ? LaunchPhase.checking.label : "Working…"
            try input.write(contentsOf: data)
            return true
        } catch { lost(error.localizedDescription); return false }
    }

    func resultText(for domain: String) -> String { domainResults[domain] ?? "" }
    private func setResult(_ domain: String, _ text: String) {
        if !domain.isEmpty { domainResults[domain] = text }
    }

    private func decode<T: Decodable>(_ value: Any, as type: T.Type = T.self) throws -> T {
        try JSONDecoder().decode(type, from: JSONSerialization.data(withJSONObject: value, options: .fragmentsAllowed))
    }

    func receive(_ line: String) {
        guard let data = line.data(using: .utf8),
              let event = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              event["version"] as? Int == 1, let id = event["id"] as? String,
              let kind = event["kind"] as? String else { lost("Invalid helper protocol. Reconnect to continue."); return }
        if let command = controls[id] {
            guard kind == "result", let outcome = event["outcome"] as? String,
                  ["success", "failure", "cancelled"].contains(outcome) else { lost("Invalid control response"); return }
            controls.removeValue(forKey: id)
            if outcome != "success" {
                activity.append(event["error"] as? String ?? "Control request failed")
                if command == "launch-choice" { cancelOperation() }
            }
            return
        }
        guard let context = control.state.operation?.context, context.id == id else {
            lost("Unexpected helper response. Reconnect to continue."); return
        }
        let payload = event["data"] as? [String: Any] ?? [:]
        do {
            switch kind {
            case "log", "progress":
                if let text = payload["stage"] as? String {
                    activity.append(text)
                    if kind == "progress", context.command != .launch, !stopping { activity.message = text }
                }
                if kind == "progress" { activity.progress = payload["percent"] as? Double }
                return
            case "phase":
                guard let raw = payload["phase"] as? String, let phase = LaunchPhase(rawValue: raw),
                      control.advance(id: id, phase: phase) else { throw ProtocolFailure.invalid }
                activity.message = stopping ? "Stopping…" : phase.label
                activity.progress = nil
                return
            case "blockers":
                let findings: [CompatibilityFinding] = try decode(payload["findings"] ?? NSNull())
                setResult(context.domain, findings.map { "\($0.modTitle): \($0.relativePath) — \($0.reason)" }.joined(separator: "\n"))
                return
            case "recovery":
                recovery = try decodeRecovery(payload)
                return
            case "status":
                guard context.command == .launch, payload["exitCode"] is Int else { throw ProtocolFailure.invalid }
                return
            case "result": break
            default: throw ProtocolFailure.invalid
            }
            guard let outcome = event["outcome"] as? String, ["success", "failure", "cancelled"].contains(outcome) else { throw ProtocolFailure.invalid }
            // Decode and validate before leaving the active state or committing any loaded data.
            if outcome == "success" { try applyResult(context, payload) }
            else {
                let message = outcome == "cancelled" ? "Cancelled" : event["error"] as? String ?? "Operation failed"
                setResult(context.domain, message)
                activity.append(message)
                activity.message = message
                if let domain = context.command.dataDomain { dataStates[domain] = .failed(message) }
            }
            if context.command == .launch && control.state.operation?.phase == .running { completedLaunchCount += 1 }
            guard control.complete(id: id) != nil else { throw ProtocolFailure.invalid }
            if outcome == "success" { activity.message = "Ready" }
            scheduleRefreshes(after: context.command, success: outcome == "success")
            drainRefreshes()
        } catch { lost("Invalid helper response: \(error.localizedDescription). Reconnect to continue.") }
    }

    private enum ProtocolFailure: Error { case invalid }
    private func decodeRecovery(_ payload: [String: Any]) throws -> PatchRecovery? {
        guard let value = payload["recovery"] else { throw ProtocolFailure.invalid }
        return value is NSNull ? nil : try decode(value)
    }

    private func applyResult(_ request: RequestContext, _ payload: [String: Any]) throws {
        switch request.command {
        case .preflight:
            let discovered: SetupInput = try decode(payload["setup"] ?? NSNull())
            guard let errors = payload["errors"] as? [String] else { throw ProtocolFailure.invalid }
            setup = discovered; preflightErrors = errors; persist()
            setResult(request.domain, errors.isEmpty ? "" : "Setup needs attention")
        case .status:
            let loaded: [Product] = try decode(payload["products"] ?? NSNull())
            let recovered = try decodeRecovery(payload)
            let installed = try readPackage(payload["package"])
            products = loaded; package = installed; recovery = recovered
        case .build, .packageUpdate, .packageRemove:
            let loaded: [Product] = try decode(payload["products"] ?? NSNull())
            let installed = request.command == .build ? nil : try readPackage(payload["package"])
            products = loaded
            dataStates["products"] = .loaded
            if let installed { package = installed }
            setResult(request.domain, request.command == .packageUpdate ? "Retro Rewind updated — rebuild required" : "")
        case .install:
            package = try readPackage(payload)
            setResult(request.domain, "Retro Rewind installed")
        case .packageState:
            guard let installed = payload["installed"] as? Bool, let reachable = payload["serverReachable"] as? Bool,
                  let outdated = payload["outOfDate"] as? Bool else { throw ProtocolFailure.invalid }
            package = PackageInfo(installed: installed, version: payload["version"] as? String ?? "",
                                  latest: payload["latest"] as? String ?? "", outOfDate: outdated, serverReachable: reachable)
            if request.feedback { setResult(request.domain, !reachable ? "Update check failed — couldn't reach the server" :
                !installed ? "Retro Rewind is not installed" : outdated ? "Update available: \(package.latest)" : "Retro Rewind is up to date") }
        case .configRead, .configWrite:
            settings = try decode(payload)
        case .patchesRestore:
            recovery = try decodeRecovery(payload)
            setResult(request.domain, "Previous patches restored")
        case .launch:
            let loaded: [Product] = try decode(payload["products"] ?? NSNull())
            let loadedSettings: RuntimeSettings = try decode(payload["settings"] ?? NSNull())
            guard let exit = payload["exitCode"] as? Int, exit == 0 else { throw ProtocolFailure.invalid }
            products = loaded; settings = loadedSettings
            dataStates["settings"] = .loaded; dataStates["products"] = .loaded
            setResult(request.domain, "")
        case .modsList, .modsImport, .modsEnabled, .modsMove, .modsReorder, .modsRemove, .modsInstall:
            mods = try decode(payload["mods"] ?? NSNull())
            if request.command != .modsList { setResult("mods", "") }
            if request.command == .modsInstall { setResult("catalog", "") }
        case .modsPreview:
            modPreview = try decode(payload["files"] ?? NSNull())
        case .modsSearch:
            let page: CatalogSearchPage = try decode(payload)
            guard let requestedPage = request.page, let query = request.query else { throw ProtocolFailure.invalid }
            if requestedPage == 1 { catalogResults = page.results }
            else {
                var seen = Set(catalogResults.map(\.id))
                catalogResults += page.results.filter { seen.insert($0.id).inserted }
            }
            catalogQuery = query; catalogPage = requestedPage; catalogComplete = page.isComplete
            setResult("catalog", "")
        case .modsDetails:
            catalogDetail = try decode(payload)
            setResult("catalog", "")
        }
        if let domain = request.command.dataDomain { dataStates[domain] = .loaded }
    }

    private func readPackage(_ value: Any?) throws -> PackageInfo {
        guard let payload = value as? [String: Any], let installed = payload["installed"] as? Bool else { throw ProtocolFailure.invalid }
        return PackageInfo(installed: installed, version: payload["version"] as? String ?? "")
    }

    private func scheduleRefreshes(after command: Command, success: Bool) {
        if command == .preflight && success { control.enqueue(.status); control.enqueue(.configRead); control.enqueue(.modsList) }
        if [.install, .packageUpdate, .packageRemove, .patchesRestore].contains(command) {
            control.enqueue(.status)
            if success && command != .patchesRestore { control.enqueue(.packageState) }
        }
        if command == .launch { control.enqueue(.configRead); control.enqueue(.status); control.enqueue(.modsList) }
        if command == .configWrite && !success { control.enqueue(.configRead) }
        if command.changesMods && !success { control.enqueue(.modsList) }
    }

    private func drainRefreshes() {
        if quitWhenIdle && !busy {
            close()
            finishQuit(after: process)
        } else if let next = control.nextRefresh() {
            send(next.rawValue, domain: next == .modsList ? "mods" : "")
        }
    }

    func refreshMods() {
        modPreview = nil
        control.enqueue(.modsList)
        drainRefreshes()
    }

    func modCommand(_ command: String, fields: [String: Any] = [:]) {
        guard send(command, domain: "mods", fields: fields) else { return }
        modPreview = nil
        setResult("mods", "")
    }
    func reorderMods(_ titles: [String]) { modCommand("mods-reorder", fields: ["titles": titles]) }
    var activeCatalogQuery: String { catalogQuery }

    // The catalog query survives window close/reopen. Never discard an in-flight request's data.
    func clearCatalogFeedback() {
        guard !busy else { return }
        setResult("catalog", ""); catalogDetail = nil
    }
    func reloadCatalog(_ query: String) {
        guard send("mods-search", domain: "catalog", fields: ["search": query, "page": 1]) else { return }
        catalogDetail = nil
        setResult("catalog", "")
    }
    func loadMoreCatalog() {
        guard !catalogComplete else { return }
        send("mods-search", domain: "catalog", fields: ["search": catalogQuery, "page": catalogPage + 1])
    }
    func loadModDetails(_ id: Int) {
        guard send("mods-details", domain: "catalog", fields: ["modId": id]) else { return }
        catalogDetail = nil
    }
    func installCatalogMod(detail: CatalogModDetail, title: String) {
        guard let file = detail.files.first ?? detail.archivedFiles.first else { return }
        send("mods-install", domain: "catalog", fields: ["url": file.downloadUrl, "modTitle": title,
                                                        "author": detail.author.name, "modId": detail.id])
    }
}
