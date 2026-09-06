import SwiftUI

struct ProductDetailView: View {
    @EnvironmentObject var session: Session
    let product: Product
    @State private var confirmRemove = false

    private var isRetroRewind: Bool { product.id == "retro-rewind" }
    private var productResult: String { session.resultText(for: product.id) }
    private var canPlay: Bool {
        product.ready && (!isRetroRewind || (session.package.installed && !session.package.outOfDate))
    }
    private var controlsDisabled: Bool { session.busy || !session.connected }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                Text(product.detail)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)

                actionRow

                if isRetroRewind {
                    RetroRewindCard(confirmRemove: $confirmRemove)
                }

                if !productResult.isEmpty {
                    resultCard
                }

                Label("Use Rebuild after local source edits.", systemImage: "info.circle")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(24)
            .frame(maxWidth: 640, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .center)
        }
        .navigationTitle(product.name)
        .alert("Remove Retro Rewind?", isPresented: $confirmRemove) {
            Button("Remove", role: .destructive) { session.removeRR() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This deletes the managed Retro Rewind package. You can reinstall it later.")
        }
    }

    private var actionRow: some View {
        HStack(spacing: 12) {
            Button {
                session.launch(product: product.id)
            } label: {
                Label("Play", systemImage: "play.fill")
                    .frame(minWidth: 70)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(!canPlay)

            Button {
                session.build(product: product.id)
            } label: {
                Label(product.ready ? "Rebuild" : "Build", systemImage: product.ready ? "arrow.clockwise" : "hammer")
            }
            .controlSize(.large)
            .disabled(isRetroRewind && !session.package.installed)
        }
        .disabled(controlsDisabled)
    }

    private var resultCard: some View {
        Text(productResult)
            .font(.system(.caption, design: .monospaced))
            .foregroundStyle(.secondary)
            .textSelection(.enabled)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
            .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))
    }
}

/// The Retro Rewind package manager, pulled out into its own card so it reads
/// as a distinct, self-contained unit of settings rather than a second set of
/// buttons bolted onto the product actions above it.
private struct RetroRewindCard: View {
    @EnvironmentObject var session: Session
    @Binding var confirmRemove: Bool
    private var controlsDisabled: Bool { session.busy || !session.connected }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label("Retro Rewind Package", systemImage: "shippingbox")
                .font(.headline)

            if session.package.installed {
                LabeledContent("Installed version") {
                    Text(session.package.version)
                        .foregroundStyle(.secondary)
                }
                if session.package.outOfDate {
                    LabeledContent("Latest version") {
                        Text(session.package.latest)
                            .foregroundStyle(.secondary)
                    }
                }
            } else {
                Text("Not installed")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }

            if !session.package.serverReachable {
                Label("Update check unavailable", systemImage: "exclamationmark.triangle")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }

            Divider()

            HStack {
                if !session.package.installed {
                    Button("Download and Install") { session.installRR() }
                        .buttonStyle(.borderedProminent)
                } else if session.package.outOfDate {
                    Button("Update") { session.updateRR() }
                        .buttonStyle(.borderedProminent)
                }
                Button("Check for Updates") { session.checkForRRUpdates() }
                Spacer()
                if session.package.installed {
                    Button("Remove…", role: .destructive) { confirmRemove = true }
                }
            }
            .controlSize(.small)
            .disabled(controlsDisabled)
        }
        .padding(16)
        .background(.quaternary.opacity(0.25), in: RoundedRectangle(cornerRadius: 12))
    }
}
