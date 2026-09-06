import SwiftUI

struct ProductDetailView: View {
    @EnvironmentObject var session: Session
    let product: Product

    var body: some View {
        VStack(alignment: .leading) {
            Text(product.name).font(.largeTitle)
            if product.id == "retro-rewind" {
                Text("Offline build").font(.headline)
                Text("Package: \(session.package.version.isEmpty ? "Not installed" : session.package.version)")
            }
            Text(product.detail)
            HStack {
                if product.id == "retro-rewind" && !session.package.installed {
                    Button("Download and Install RR") { session.installRR() }
                }
                Button(product.ready ? "Rebuild" : "Build") { session.build(product: product.id) }
                    .disabled(product.id == "retro-rewind" && !session.package.installed)
                Button("Play") { session.launch(product: product.id) }
                    .disabled(!product.ready)
                SettingsLink { Text("Settings") }
            }
            .disabled(session.busy || !session.connected)
            Text("Use Rebuild after local source edits.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}
