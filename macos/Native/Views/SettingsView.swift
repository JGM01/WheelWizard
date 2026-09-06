import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var session: Session
    @State private var volume = 1.0
    @State private var resolution = 1.0

    var body: some View {
        Form {
            Slider(value: $volume, in: 0...1) { Text("Volume") }
            Picker("Resolution", selection: $resolution) {
                ForEach([1.0, 1.5, 2.0, 3.0], id: \.self) { value in
                    Text("\(value, specifier: "%.1f")×").tag(value)
                }
            }
            Button("Save") { session.saveSettings(volume: volume, resolutionMultiplier: resolution) }
        }
        .formStyle(.grouped)
        .disabled(session.busy || !session.connected)
        .onAppear {
            volume = session.settings.volume
            resolution = session.settings.resolutionMultiplier
        }
    }
}
