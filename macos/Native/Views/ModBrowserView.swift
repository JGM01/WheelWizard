import SwiftUI
import AppKit

// GameBanana catalog browser: search list on the left, details + install on the right.
// It talks to the bundled helper through Session (mods-search / mods-details / mods-install)
// and lives in its own resizable "Mod Catalog" window (see WheelWizardApp).
struct ModBrowserView: View {
    @EnvironmentObject var session: Session
    @Environment(\.dismiss) private var dismiss
    @State private var searchText = ""
    @State private var selection: Int?
    @State private var patchesOnly = false

    private var busy: Bool { session.busy || session.catalogBusy }
    private var error: String { session.resultText(for: "catalog") }
    private var installedIDs: Set<Int> { Set(session.mods.filter { $0.modID > 0 }.map(\.modID)) }

    private var detail: CatalogModDetail? {
        guard let selection, session.catalogDetail?.id == selection else { return nil }
        return session.catalogDetail
    }

    var body: some View {
        HSplitView {
            listPane
            detailPane
        }
        .onAppear {
            searchText = session.activeCatalogQuery
            if !session.busy {
                session.clearCatalogFeedback()
            }
            if session.catalogResults.isEmpty && !session.busy && session.connected {
                session.reloadCatalog("")
            }
        }
        .onChange(of: selection) { _, newValue in
            guard let newValue, session.catalogDetail?.id != newValue else { return }
            session.loadModDetails(newValue)
        }
    }

    private var listPane: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                TextField("Search mods…", text: $searchText)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { session.reloadCatalog(searchText.trimmingCharacters(in: .whitespaces)) }
                Button {
                    session.reloadCatalog(searchText.trimmingCharacters(in: .whitespaces))
                } label: {
                    Image(systemName: "magnifyingglass")
                }
                .help("Search GameBanana (empty shows featured mods)")
                Button {
                    dismiss()
                } label: {
                    Image(systemName: "xmark.circle.fill")
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .help("Close")
            }
            Text("Powered by GameBanana")
                .font(.caption)
                .foregroundStyle(.secondary)
            Toggle("Patches only", isOn: $patchesOnly)
                .toggleStyle(.switch)
                .controlSize(.small)
            Divider()
            if busy && session.catalogResults.isEmpty {
                Spacer()
                ProgressView("Searching…")
                    .frame(maxWidth: .infinity)
                Spacer()
            } else if session.catalogResults.isEmpty {
                Spacer()
                ContentUnavailableView("No Mods Found", systemImage: "magnifyingglass", description: Text("Try a different search term."))
                Spacer()
            } else {
                List(selection: $selection) {
                    ForEach(listRows) { row in
                        rowView(row).tag(row.id)
                    }
                    if !session.catalogComplete {
                        HStack {
                            Spacer()
                            if busy {
                                ProgressView().controlSize(.small)
                            } else {
                                Button("Load More") { session.loadMoreCatalog() }
                            }
                            Spacer()
                        }
                        .listRowSeparator(.hidden)
                    }
                }
            }
            if !error.isEmpty {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .frame(minWidth: 340)
    }

    private var listRows: [CatalogMod] {
        let all = session.catalogResults
        guard patchesOnly else { return all }
        return all.filter(\.usesPatches)
    }

    private func rowView(_ mod: CatalogMod) -> some View {
        HStack(spacing: 10) {
            thumbnail(mod.imageUrl, size: 46)
            VStack(alignment: .leading, spacing: 2) {
                Text(mod.name)
                    .lineLimit(1)
                Text("\(mod.author) · v\(mod.version)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 4)
            if mod.usesPatches {
                Text("Patches")
                    .font(.caption2)
                    .padding(.horizontal, 5)
                    .padding(.vertical, 1)
                    .background(Capsule().fill(Color.accentColor.opacity(0.18)))
            }
            if installedIDs.contains(mod.id) {
                Image(systemName: "checkmark.seal.fill")
                    .foregroundStyle(.green)
                    .help("Installed")
            }
        }
        .padding(.vertical, 2)
    }

    private var detailPane: some View {
        Group {
            if let detail {
                detailContent(detail)
            } else if selection != nil && busy {
                VStack {
                    Spacer()
                    ProgressView("Loading details…")
                    Spacer()
                }
            } else if !error.isEmpty {
                VStack(spacing: 10) {
                    Image(systemName: "exclamationmark.triangle")
                        .font(.largeTitle)
                        .foregroundStyle(.orange)
                    Text(error)
                        .foregroundStyle(.red)
                        .multilineTextAlignment(.center)
                        .textSelection(.enabled)
                }
                .padding(24)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ContentUnavailableView("Select a Mod", systemImage: "shippingbox", description: Text("Choose a result on the left to see details."))
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .frame(minWidth: 420)
    }

    private func detailContent(_ detail: CatalogModDetail) -> some View {
        let installed = installedIDs.contains(detail.id)
        let hasFile = !detail.files.isEmpty || !detail.archivedFiles.isEmpty
        let text = Self.plainText(fromHTML: detail.text)
        return ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                if let first = detail.images.first {
                    thumbnail(first, size: 260)
                        .frame(maxWidth: .infinity)
                }
                HStack(alignment: .firstTextBaseline) {
                    Text(detail.name).font(.title2).bold()
                    Spacer()
                    Text("v\(detail.version)")
                        .foregroundStyle(.secondary)
                }
                Text(detail.author.name)
                    .foregroundStyle(.secondary)
                HStack(spacing: 14) {
                    Label("\(detail.likeCount)", systemImage: "hand.thumbsup")
                    Label("\(detail.viewCount)", systemImage: "eye")
                    Label("\(detail.downloadCount)", systemImage: "arrow.down.circle")
                }
                .font(.callout)
                .foregroundStyle(.secondary)
                HStack {
                    if installed {
                        Label("Installed", systemImage: "checkmark.seal.fill")
                            .foregroundStyle(.green)
                    } else {
                        Button {
                            session.installCatalogMod(detail: detail, title: detail.name)
                        } label: {
                            Label("Download and Install", systemImage: "arrow.down.circle")
                        }
                        .buttonStyle(.borderedProminent)
                        .disabled(!hasFile || busy)
                    }
                    Spacer()
                    if let url = URL(string: detail.profileUrl) {
                        Link("View on GameBanana", destination: url)
                            .font(.callout)
                    }
                }
                .padding(.top, 2)
                Divider()
                if !text.isEmpty {
                    Text(text)
                        .font(.callout)
                        .textSelection(.enabled)
                }
            }
            .padding(18)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private func thumbnail(_ urlString: String?, size: CGFloat) -> some View {
        Group {
            if let urlString, let url = URL(string: urlString) {
                AsyncImage(url: url) { phase in
                    switch phase {
                    case .success(let image):
                        image.resizable().scaledToFill()
                    case .failure:
                        placeholder
                    default:
                        ProgressView()
                    }
                }
                .frame(width: size, height: size)
                .clipped()
                .clipShape(RoundedRectangle(cornerRadius: 6))
            } else {
                placeholder
            }
        }
        .frame(width: size, height: size)
    }

    private var placeholder: some View {
        RoundedRectangle(cornerRadius: 6)
            .fill(Color.secondary.opacity(0.15))
            .overlay(Image(systemName: "shippingbox").foregroundStyle(.secondary))
    }

    static func plainText(fromHTML html: String) -> String {
        guard let data = html.data(using: .utf8) else { return "" }
        guard let attributed = try? NSAttributedString(
            data: data,
            options: [.documentType: NSAttributedString.DocumentType.html, .characterEncoding: String.Encoding.utf8.rawValue],
            documentAttributes: nil
        ) else { return "" }
        let text = attributed.string.trimmingCharacters(in: .whitespacesAndNewlines)
        return text
    }
}
