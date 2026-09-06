import SwiftUI

struct ContentView: View {
    @EnvironmentObject var session: Session
    @State private var selected: String? = "base"

    var body: some View {
        NavigationSplitView {
            List(selection: $selected) {
                ForEach(session.products) { product in
                    Label(product.name, systemImage: "flag.checkered").tag(product.id)
                }
                Label("Setup", systemImage: "gearshape").tag("setup")
            }
            .navigationTitle("WheelWizard")
        } detail: {
            VStack(alignment: .leading) {
                if selected == "setup" {
                    SetupView()
                } else if let product = session.products.first(where: { $0.id == selected }) {
                    ProductDetailView(product: product)
                }
                StatusView()
                Spacer(minLength: 0)
            }
            .padding()
        }
    }
}
