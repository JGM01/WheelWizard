import SwiftUI

enum NavigationItem: Hashable {
    case product(Product.ID)
    case logs
    case mods
}

struct ContentView: View {
    @EnvironmentObject var session: Session
    @State private var selection: NavigationItem? = .product("base")

    var body: some View {
        NavigationSplitView {
            sidebar
        } detail: {
            detail
        }
        .toolbar {
            ToolbarItem(placement: .automatic) {
                SettingsLink()
            }
        }
    }

    private var sidebar: some View {
        List(selection: $selection) {
            Section("Library") {
                ForEach(session.products) { product in
                    ProductRow(product: product)
                        .tag(NavigationItem.product(product.id))
                }
            }
            Section("Tools") {
                Label("Mods", systemImage: "shippingbox")
                    .tag(NavigationItem.mods)
                Label("Activity Log", systemImage: "terminal")
                    .tag(NavigationItem.logs)
            }
        }
        .navigationSplitViewColumnWidth(min: 200, ideal: 230, max: 280)
        .navigationTitle("WheelWizard")
    }

    @ViewBuilder
    private var detail: some View {
        Group {
            switch selection {
            case .product(let id):
                if let product = session.products.first(where: { $0.id == id }) {
                    ProductDetailView(product: product)
                } else {
                    emptyState
                }
            case .mods:
                ModsView()
            case .logs:
                LogPane()
            case .none:
                emptyState
            }
        }
        .safeAreaInset(edge: .bottom) {
            StatusView()
        }
    }

    private var emptyState: some View {
        ContentUnavailableView(
            "Select a Game",
            systemImage: "gamecontroller",
            description: Text("Choose a title from the library to view its details.")
        )
    }
}

/// A single row in the library list. Kept as its own type (rather than inline
/// in the `ForEach`) so the readiness dot and per-product tint don't clutter
/// `ContentView`'s body — each view should read as "what," not "how."
private struct ProductRow: View {
    let product: Product
    private var isRetroRewind: Bool { product.id == "retro-rewind" }

    var body: some View {
        HStack {
            Label {
                Text(product.name)
            } icon: {
                Image(systemName: "flag.checkered")
                    .foregroundStyle(isRetroRewind ? .purple : .accentColor)
            }
            Spacer()
            Circle()
                .fill(product.ready ? Color.green : Color.secondary.opacity(0.3))
                .frame(width: 6, height: 6)
        }
        .contentShape(Rectangle())
    }
}

/// The activity log, restyled as a lightweight console: monospaced, filterable,
/// and living on a distinct background so it visually reads as "output" rather
/// than another content pane.
private struct LogPane: View {
    @EnvironmentObject var activity: Activity
    @EnvironmentObject var session: Session
    @State private var filterText = ""

    private var filteredLines: [String] {
        guard !filterText.isEmpty else { return activity.log }
        return activity.log.filter { $0.localizedCaseInsensitiveContains(filterText) }
    }

    var body: some View {
        ScrollView {
            Text(filteredLines.joined(separator: "\n"))
                .font(.system(.caption, design: .monospaced))
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(12)
        }
        .background(Color(nsColor: .textBackgroundColor))
        .navigationTitle("Activity Log")
        .searchable(text: $filterText, placement: .toolbar, prompt: "Filter log")
        .toolbar {
            ToolbarItemGroup {
                Button {
                    session.revealLogs()
                } label: {
                    Label("Open Logs", systemImage: "folder")
                }
                .help("Open Logs in Finder")

                Button {
                    session.revealRuntimeLogs()
                } label: {
                    Label("Open Runtime Logs", systemImage: "terminal")
                }
                .help("Open Runtime Logs in Finder")
            }
        }
    }
}
