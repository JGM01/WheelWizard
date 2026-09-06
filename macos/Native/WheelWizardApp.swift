import SwiftUI

@main
struct WheelWizardApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var session = Session()

    var body: some Scene {
        WindowGroup("WheelWizard") {
            ContentView()
                .environmentObject(session)
                .environmentObject(session.activity)
                .onAppear { delegate.session = session }
        }
        Settings {
            SettingsView()
                .environmentObject(session)
        }
    }
}
