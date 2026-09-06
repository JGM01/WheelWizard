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
            TabView {
                SetupView()
                    .tabItem { Label("Prerequisites", systemImage: "wrench.and.screwdriver") }
                SettingsView()
                    .tabItem { Label("Playback", systemImage: "slider.horizontal.3") }
            }
            .environmentObject(session)
        }
    }
}
