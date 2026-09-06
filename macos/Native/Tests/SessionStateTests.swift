import Foundation

@main
struct SessionStateTests {
    static func request(_ command: Command, id: String = UUID().uuidString) -> RequestContext {
        RequestContext(id: id, command: command, domain: "test", product: command == .launch ? "retro-rewind" : nil,
                       query: nil, page: nil, feedback: false)
    }
    static func main() {
        for command in Command.allCases {
            var session = SessionControl()
            let current = request(command)
            precondition(!session.start(current))
            session.connecting()
            precondition(session.state.busy && !session.state.connected)
            session.connected()
            precondition(session.start(current))
            precondition(!session.start(request(.modsSearch)))
            precondition(session.complete(id: "stale") == nil)
            if command != .launch { precondition(!session.advance(id: current.id, phase: .running)) }
            session.stop()
            precondition(session.state.stopping && session.state.busy)
            precondition(session.complete(id: current.id)?.command == command)
            precondition(!session.state.busy)
        }
        var session = SessionControl()
        session.connected()
        let launch = request(.launch)
        precondition(session.start(launch))
        precondition(!session.advance(id: launch.id, phase: .running))
        for phase in [LaunchPhase.checking, .awaitingChoice, .choiceSubmitted, .preparing, .publishing, .starting, .running] {
            precondition(session.advance(id: launch.id, phase: phase))
        }
        session.enqueue(.status); session.enqueue(.status); session.enqueue(.configRead)
        session.enqueue(.modsRemove) // mutations never enter the follow-up queue
        precondition(session.nextRefresh() == nil)
        precondition(session.complete(id: launch.id) != nil)
        precondition(session.nextRefresh() == .status)
        precondition(session.nextRefresh() == .configRead)
        precondition(session.nextRefresh() == nil)
        precondition(session.start(request(.build)))
        session.enqueue(.status)
        session.stop(quit: true)
        precondition(session.quitRequested && session.refreshes.isEmpty)
        session.enqueue(.configRead)
        precondition(session.refreshes.isEmpty)
        session.disconnect("lost")
        precondition(!session.state.connected && !session.state.busy)
        precondition(session.complete(id: launch.id) == nil)
        print("Session state tests passed (all \(Command.allCases.count) operations, launch ordering, cancellation, queue, quit, disconnect)")
    }
}
