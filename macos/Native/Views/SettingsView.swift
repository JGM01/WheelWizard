import SwiftUI

/// The app's Settings scene.
///
/// This used to hold two tabs — Prerequisites and Playback — switched with a
/// native `TabView`. Playback (volume/resolution) now lives on each game's
/// own detail page instead: a setting you'd only ever want to touch right
/// before playing is far more likely to get found sitting next to the Play
/// button than behind a menu item. That leaves exactly one thing in
/// Settings, so there's no tab strip to desync or resize between anymore —
/// the window can simply size itself to its content.
struct SettingsView: View {
    var body: some View {
        SetupView()
            .frame(width: 520)
            .fixedSize(horizontal: false, vertical: true)
    }
}
