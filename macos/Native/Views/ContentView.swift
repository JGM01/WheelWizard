import SwiftUI

enum NavigationItem: Hashable {
    case product(Product.ID)
    case logs
    case mods
    case prerequisites
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
        .alert("Existing Patches Found", isPresented: Binding(get: { session.awaitingPatchChoice }, set: { _ in })) {
            Button("Delete", role: .destructive) { session.choosePatches(delete: true) }
            Button("Keep") { session.choosePatches(delete: false) }
            Button("Cancel", role: .cancel) { session.cancelOperation() }
        } message: {
            Text("All mods are disabled. Delete existing runtime patches, or keep them active for this launch? Retained patches will be checked for conversion requirements.")
        }
    }

    private var sidebar: some View {
        List(selection: $selection) {
            Section("Games") {
                ForEach(session.products) { product in
                    ProductRow(product: product)
                        .tag(NavigationItem.product(product.id))
                }
            }
            Section("Tools") {
                Label("Mods", systemImage: "shippingbox")
                    .tag(NavigationItem.mods)
                Label("Activity Log", systemImage: "text.document")
                    .tag(NavigationItem.logs)
                Label("Prerequisites", systemImage: "wrench.and.screwdriver")
                    .tag(NavigationItem.prerequisites)
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
            case .prerequisites:
                SetupView()
                    .navigationTitle("Prerequisites")
            case .none:
                emptyState
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        // Fixed-height, always-mounted bar — see StatusView for why this
        // matters. The detail pane's frame never changes because of it.
        .safeAreaInset(edge: .bottom, spacing: 0) { StatusView() }
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
            StatusDot(on: product.ready)
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
        Group {
            if activity.log.isEmpty {
                ContentUnavailableView(
                    "No Activity Yet",
                    systemImage: "terminal",
                    description: Text("Operations will appear here as they run.")
                )
            } else if !filterText.isEmpty && filteredLines.isEmpty {
                ContentUnavailableView.search(text: filterText)
            } else {
                ScrollView {
                    Text(filteredLines.joined(separator: "\n"))
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(12)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
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
