import Foundation

// The dependency graph is acyclic; this state machine intentionally returns to idle.
// Only an active launch can own a launch phase or a pending patch decision.
enum Command: String, CaseIterable {
    case preflight, status, install, build, launch
    case packageUpdate = "package-update", packageRemove = "package-remove", packageState = "package-state"
    case configRead = "config-read", configWrite = "config-write", patchesRestore = "patches-restore"
    case modsList = "mods-list", modsImport = "mods-import", modsEnabled = "mods-enabled"
    case modsMove = "mods-move", modsReorder = "mods-reorder", modsRemove = "mods-remove"
    case modsPreview = "mods-preview", modsSearch = "mods-search", modsDetails = "mods-details", modsInstall = "mods-install"

    var changesMods: Bool { [.modsImport, .modsEnabled, .modsMove, .modsReorder, .modsRemove, .modsInstall].contains(self) }
    var catalog: Bool { [.modsSearch, .modsDetails, .modsInstall].contains(self) }
    var refresh: Bool { [.status, .configRead, .modsList, .packageState].contains(self) }
    var dataDomain: String? {
        if changesMods || self == .modsList { return "mods" }
        switch self {
        case .preflight: return "setup"
        case .status: return "products"
        case .configRead, .configWrite: return "settings"
        case .packageState: return "package"
        case .modsSearch: return "catalog"
        case .modsDetails: return "detail"
        default: return nil
        }
    }
}

struct RequestContext {
    let id: String
    let command: Command
    let domain: String
    let product: String?
    let query: String?
    let page: Int?
    let feedback: Bool
}

enum LaunchPhase: String {
    case checking, awaitingChoice = "awaiting-choice", choiceSubmitted = "choice-submitted", preparing, publishing, starting, running
    var label: String {
        switch self {
        case .checking: return "Checking launch prerequisites…"
        case .awaitingChoice: return "Choose whether to keep existing patches"
        case .choiceSubmitted: return "Checking patch choice…"
        case .preparing: return "Preparing mods…"
        case .publishing: return "Publishing patches…"
        case .starting: return "Starting game…"
        case .running: return "Game running"
        }
    }
    func accepts(_ next: LaunchPhase) -> Bool {
        switch (self, next) {
        case (.checking, .checking), (.checking, .awaitingChoice), (.checking, .preparing), (.checking, .starting),
             (.awaitingChoice, .choiceSubmitted), (.choiceSubmitted, .preparing), (.choiceSubmitted, .starting), (.preparing, .publishing),
             (.publishing, .starting), (.starting, .running): return true
        default: return false
        }
    }
}

enum ActiveOperation {
    case task(RequestContext)
    case launch(RequestContext, LaunchPhase)
    var context: RequestContext {
        switch self { case .task(let request), .launch(let request, _): return request }
    }
    var phase: LaunchPhase? {
        if case .launch(_, let phase) = self { return phase }; return nil
    }
}

enum SessionState {
    case disconnected(String), connecting, idle, executing(ActiveOperation), stopping(ActiveOperation)
    var operation: ActiveOperation? {
        switch self { case .executing(let op), .stopping(let op): return op; default: return nil }
    }
    var connected: Bool {
        switch self { case .idle, .executing, .stopping: return true; default: return false }
    }
    var busy: Bool { if case .connecting = self { return true }; return operation != nil }
    var stopping: Bool { if case .stopping = self { return true }; return false }
}

enum LoadState: Equatable { case unavailable, loading, loaded, failed(String) }

// Pure transition logic can be tested without starting a helper or an AppKit application.
struct SessionControl {
    private(set) var state = SessionState.disconnected("Not connected")
    private(set) var refreshes: [Command] = []
    private(set) var quitRequested = false
    mutating func connecting() { state = .connecting }
    mutating func connected() { state = .idle }
    mutating func disconnect(_ reason: String) { state = .disconnected(reason); refreshes = [] }
    mutating func start(_ request: RequestContext) -> Bool {
        guard case .idle = state, !quitRequested else { return false }
        state = .executing(request.command == .launch ? .launch(request, .checking) : .task(request))
        return true
    }
    mutating func advance(id: String, phase: LaunchPhase) -> Bool {
        guard let op = state.operation, op.context.id == id,
              case .launch(let request, let previous) = op, previous.accepts(phase) else { return false }
        state = state.stopping ? .stopping(.launch(request, phase)) : .executing(.launch(request, phase))
        return true
    }
    mutating func complete(id: String) -> RequestContext? {
        guard let op = state.operation, op.context.id == id else { return nil }
        state = .idle
        return op.context
    }
    mutating func stop(quit: Bool = false) {
        if quit { quitRequested = true; refreshes = [] }
        if let operation = state.operation { state = .stopping(operation) }
    }
    mutating func enqueue(_ command: Command) {
        guard command.refresh, !quitRequested, !refreshes.contains(command) else { return }
        refreshes.append(command)
    }
    mutating func nextRefresh() -> Command? {
        guard case .idle = state, !quitRequested, !refreshes.isEmpty else { return nil }
        return refreshes.removeFirst()
    }
}
