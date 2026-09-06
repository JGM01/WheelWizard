import SwiftUI

/// The app's Settings scene. macOS renders a `TabView` whose children carry
/// `.tabItem` as the classic icon-over-label preferences switcher — this is
/// the same mechanism System Settings panes use, so no custom chrome is
/// needed to get that look. `SetupView` (prerequisites/tool paths) and
/// playback options now live together here, since they're both
/// "configure the app once" concerns rather than day-to-day actions.
struct SettingsView: View {
    var body: some View {
        TabView {
            SetupView()
                .tabItem {
                    Label("Prerequisites", systemImage: "wrench.and.screwdriver")
                }

            PlaybackSettingsView()
                .tabItem {
                    Label("Playback", systemImage: "slider.horizontal.3")
                }
        }
        .frame(width: 480)
        .fixedSize(horizontal: false, vertical: true)
    }
}

private struct PlaybackSettingsView: View {
    @EnvironmentObject var session: Session
    @State private var volume = 1.0
    @State private var resolution = 1.0
    @State private var baseline = RuntimeSettings()

    var body: some View {
        Form {
            Section {
                Slider(value: $volume, in: 0...1) {
                    Text("Volume")
                }
            }
            Section {
                Picker("Resolution", selection: $resolution) {
                    ForEach([1.0, 1.5, 2.0, 3.0], id: \.self) { value in
                        Text("\(value, specifier: "%.1f")×").tag(value)
                    }
                }
            }
            Section {
                HStack {
                    Spacer()
                    Button("Save") {
                        session.saveSettings(volume: volume, resolutionMultiplier: resolution)
                    }
                    .buttonStyle(.borderedProminent)
                }
            }
        }
        .formStyle(.grouped)
        .padding(.top, 8)
        .disabled(session.busy || !session.connected || !session.settingsLoaded)
        .onAppear { load(session.settings) }
        .onChange(of: session.completedLaunchCount) { _, _ in load(session.settings) }
        .onChange(of: session.settings) { _, value in
            if volume == baseline.volume && resolution == baseline.resolutionMultiplier {
                load(value)
            } else if value.volume == volume && value.resolutionMultiplier == resolution {
                baseline = value
            }
        }
    }

    private func load(_ value: RuntimeSettings) {
        volume = value.volume; resolution = value.resolutionMultiplier; baseline = value
    }
}
