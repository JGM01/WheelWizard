import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    weak var session: Session?

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let session else { return .terminateNow }
        if session.busy {
            let alert = NSAlert()
            alert.messageText = "Stop the active operation and quit?"
            alert.informativeText = "The helper will stop its build or game process and finish any publication rollback before quitting."
            alert.addButton(withTitle: "Keep Running")
            alert.addButton(withTitle: "Stop and Quit")
            guard alert.runModal() == .alertSecondButtonReturn else { return .terminateCancel }
            session.quitWhenIdle = true
            session.cancelOperation()
            return .terminateLater
        }
        session.close()
        return .terminateNow
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}
