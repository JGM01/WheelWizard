import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    weak var session: Session?
    weak var mainWindow: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Preserve the single-window quit semantics: closing the main window quits the app even when
        // the Mod Catalog window is still open. With only the main window visible the normal
        // last-window-closed termination already handles it, so we only step in when others remain.
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: nil, queue: .main) { [weak self] note in
            Task { @MainActor in
                guard let self, let closing = note.object as? NSWindow, closing === self.mainWindow else { return }
                let othersVisible = NSApp.windows.contains { $0 !== closing && $0.isVisible }
                if othersVisible {
                    NSApp.terminate(nil)
                }
            }
        }
    }

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

// Hands the hosting NSWindow to a closure once the view is in the hierarchy (used to tag the main window).
struct WindowAccessor: NSViewRepresentable {
    let onAppear: (NSWindow?) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async { onAppear(view.window) }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}
}

