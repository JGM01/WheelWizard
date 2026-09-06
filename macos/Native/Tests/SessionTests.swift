import AppKit
import Foundation

@main
struct SessionTests {
    @MainActor static func main() throws {
        let pipe = Pipe()
        let session = Session(transport: pipe.fileHandleForWriting)
        func result(_ payload: [String: Any], outcome: String = "success") throws {
            let id = session.control.state.operation!.context.id
            let event: [String: Any] = ["version": 1, "id": id, "kind": "result", "outcome": outcome, "data": payload]
            session.receive(String(decoding: try JSONSerialization.data(withJSONObject: event), as: UTF8.self))
        }
        session.reloadCatalog("first")
        try result(["results": [["id": 1, "name": "Example", "version": "1", "author": "Author", "profileUrl": "https://example.test",
                                "likeCount": 0, "viewCount": 0, "usesPatches": true, "tags": []]], "isComplete": false])
        precondition(session.catalogResults.count == 1 && session.activeCatalogQuery == "first")
        session.loadModDetails(1)
        session.reloadCatalog("rejected")
        precondition(session.catalogResults.count == 1 && session.activeCatalogQuery == "first")
        precondition(session.control.state.operation?.context.command == .modsDetails)
        try result([:], outcome: "failure")
        precondition(!session.catalogBusy && !session.busy)
        session.saveSettings(volume: 0.4, resolutionMultiplier: 2)
        precondition(session.settings.volume == 1) // no optimistic mutation
        session.saveSettings(volume: 0.8, resolutionMultiplier: 3)
        try result(["volume": 0.4, "resolutionMultiplier": 2])
        precondition(session.settings.volume == 0.4 && session.settingsLoaded)
        session.send("mods-list")
        try result(["mods": []])
        precondition(session.modsLoaded)
        session.send("mods-list")
        try result(["mods": "malformed"])
        precondition(!session.connected && !session.modsLoaded)
        let state = session.control.state
        if case .disconnected = state {} else { preconditionFailure("Malformed result must disconnect") }
        session.reloadCatalog("cannot run")
        precondition(!session.busy)
        print("Session integration tests passed (catalog rejection, confirmed settings, malformed payloads, disconnected admission)")
    }
}
