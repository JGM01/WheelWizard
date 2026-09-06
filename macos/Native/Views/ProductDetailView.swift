import SwiftUI

struct ProductDetailView: View {
    @EnvironmentObject var session: Session
    let product: Product
    @State private var confirmRemove = false
    @State private var confirmRestore = false

    private var isRetroRewind: Bool { product.id == "retro-rewind" }
    private var productResult: String { session.resultText(for: product.id) }
    private var canPlay: Bool {
        product.ready && session.dataStates["products"] == .loaded && (!isRetroRewind || (session.package.installed && !session.package.outOfDate && session.recovery == nil && session.modsLoaded && !session.mods.contains { $0.isEnabled && $0.requiresAttention }))
    }
    private var controlsDisabled: Bool { session.busy || !session.connected }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                Text(product.detail)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)

                actionRow

                if isRetroRewind, session.mods.contains(where: { $0.isEnabled && $0.requiresAttention }) {
                    Label("Some enabled mods need attention. Open Mods to inspect or disable them before playing.", systemImage: "exclamationmark.triangle")
                        .foregroundStyle(.orange)
                }
                if isRetroRewind, let recovery = session.recovery {
                    VStack(alignment: .leading, spacing: 8) {
                        Label("Patch recovery required", systemImage: "exclamationmark.triangle")
                        Text(recovery.message)
                        Text(recovery.recordPath).font(.caption).textSelection(.enabled)
                        Button("Restore previous patches…") { confirmRestore = true }
                            .disabled(controlsDisabled)
                    }
                }

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
        .alert("Restore previous patches?", isPresented: $confirmRestore) {
            Button("Restore previous patches") { session.restorePatches() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Restore the complete patch set from before the interrupted publication. If recovery cannot finish, the backup will be preserved and Play will remain blocked.")
        }
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
            .disabled(isRetroRewind && (!session.package.installed || session.recovery != nil))
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
                        .disabled(session.recovery != nil)
                        .buttonStyle(.borderedProminent)
                } else if session.package.outOfDate {
                    Button("Update") { session.updateRR() }
                        .disabled(session.recovery != nil)
                        .buttonStyle(.borderedProminent)
                }
                Button("Check for Updates") { session.checkForRRUpdates() }
                Spacer()
                if session.package.installed {
                    Button("Remove…", role: .destructive) { confirmRemove = true }
                        .disabled(session.recovery != nil)
                }
            }
            .controlSize(.small)
            .disabled(controlsDisabled)
        }
        .padding(16)
        .background(.quaternary.opacity(0.25), in: RoundedRectangle(cornerRadius: 12))
    }
}
