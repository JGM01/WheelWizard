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
                .background(WindowAccessor { window in
                    delegate.mainWindow = window
                })
                .onAppear { delegate.session = session }
        }
        Window("Mod Catalog", id: "mod-catalog") {
            ModBrowserView()
                .environmentObject(session)
                .frame(minWidth: 880, minHeight: 580)
        }
        .defaultSize(width: 980, height: 660)
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
