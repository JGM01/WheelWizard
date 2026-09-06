import SwiftUI

struct ProductDetailView: View {
    @EnvironmentObject var session: Session
    let product: Product
    @State private var confirmRemove = false
    @State private var confirmRestore = false

    private var isRetroRewind: Bool { product.id == "retro-rewind" }
    private var productResult: String { session.resultText(for: product.id) }
    private var modsNeedAttention: Bool {
        isRetroRewind && session.mods.contains { $0.isEnabled && $0.requiresAttention }
    }
    private var canPlay: Bool {
        product.ready && session.dataStates["products"] == .loaded &&
        (!isRetroRewind || (session.package.installed && !session.package.outOfDate && session.recovery == nil && session.modsLoaded && !modsNeedAttention))
    }
    private var controlsDisabled: Bool { session.busy || !session.connected }

    var body: some View {
        ScrollView {
            HStack(alignment: .top, spacing: 24) {
                primaryColumn
                    .frame(minWidth: 420, maxWidth: .infinity, alignment: .leading)
                sideColumn
                    .frame(width: 300)
            }
            .padding(24)
            .frame(maxWidth: 1000, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .center)
        }
        .navigationTitle(product.name)
        .alert("Restore Previous Patches?", isPresented: $confirmRestore) {
            Button("Restore Previous Patches") { session.restorePatches() }
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

    // MARK: - Left: what you came here to do

    private var primaryColumn: some View {
        VStack(alignment: .leading, spacing: Metrics.sectionSpacing) {
            header
            actionRow

            if isRetroRewind, let recovery = session.recovery {
                Callout(
                    kind: .warning,
                    title: "Patch Recovery Required",
                    message: recovery.message,
                    actionTitle: "Restore Previous Patches"
                ) { confirmRestore = true }
                    .disabled(controlsDisabled)
            }

            if modsNeedAttention {
                Callout(
                    kind: .warning,
                    title: "Some Mods Need Attention",
                    message: "Open Mods to inspect or disable files that need conversion before playing."
                )
            }

            if !productResult.isEmpty {
                Callout(kind: .error, title: "Something Went Wrong", message: productResult)
            }
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                StatusDot(on: product.ready)
                Text(product.ready ? "Ready" : "Not Built")
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(product.ready ? .primary : .secondary)
            }
            Text(product.detail)
                .font(.subheadline)
                .foregroundStyle(.secondary)
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

            Spacer()

            Label("Use Rebuild after local source edits.", systemImage: "info.circle")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .disabled(controlsDisabled)
    }

    // MARK: - Right: details specific to this game

    private var sideColumn: some View {
        VStack(alignment: .leading, spacing: Metrics.sectionSpacing) {
            PlaybackCard()
            if isRetroRewind {
                RetroRewindCard(confirmRemove: $confirmRemove)
            }
        }
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
        Card {
            Label("Retro Rewind Install", systemImage: "shippingbox")
                .font(.headline)

            if session.package.installed {
                LabeledContent("Installed Version") {
                    Text(session.package.version)
                        .foregroundStyle(.secondary)
                }
                if session.package.outOfDate {
                    LabeledContent("Latest Version") {
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
    }
}

/// Volume/resolution live on each game's own page now instead of a separate
/// Settings tab nobody thought to open. There's deliberately no Save button:
/// the slider commits when you let go, the resolution picker commits the
/// moment you pick, and everything locks while a game is running so there's
/// never a chance to change a setting mid-session and wonder whether it
/// "took." `RuntimeSettings` is a single app-wide value shared by both game
/// pages — that's a backend fact, not a UI one, so it simply shows the same
/// current values wherever you look at it.
private struct PlaybackCard: View {
    @EnvironmentObject var session: Session
    @State private var volume = 1.0
    @State private var resolution = 1.0
    @State private var baseline = RuntimeSettings()
    @State private var isAdjustingVolume = false

    private var locked: Bool { session.busy || !session.connected || !session.settingsLoaded }

    var body: some View {
        Card {
            VStack {
                Label("Runtime Settings", systemImage: "slider.horizontal.3")
                    .font(.headline)
                Spacer()
                if session.busy {
                    Label("Locked while running", systemImage: "lock.fill")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("Volume").font(.subheadline).foregroundStyle(.secondary)
                Slider(value: $volume, in: 0...1, onEditingChanged: { editing in
                    isAdjustingVolume = editing
                    if !editing { commit() }
                })
            }

            VStack(alignment: .leading, spacing: 6) {
                Text("Resolution").font(.subheadline).foregroundStyle(.secondary)
                Picker("", selection: $resolution) {
                    ForEach([1.0, 1.5, 2.0, 3.0], id: \.self) { value in
                        Text("\(value, specifier: "%.1f")×").tag(value)
                    }
                }
                .labelsHidden()
                .pickerStyle(.segmented)
                .onChange(of: resolution) { _, _ in commit() }
            }
        }
        .disabled(locked)
        .onAppear { load(session.settings) }
        .onChange(of: session.completedLaunchCount) { _, _ in load(session.settings) }
        .onChange(of: session.settings) { _, value in
            // Don't let a refresh from the helper clobber an edit the person
            // is actively making; once it settles, reconcile normally.
            guard !isAdjustingVolume else { return }
            if volume == baseline.volume && resolution == baseline.resolutionMultiplier {
                load(value)
            } else if value.volume == volume && value.resolutionMultiplier == resolution {
                baseline = value
            }
        }
    }

    private func commit() {
        guard !locked else { return }
        session.saveSettings(volume: volume, resolutionMultiplier: resolution)
    }

    private func load(_ value: RuntimeSettings) {
        volume = value.volume; resolution = value.resolutionMultiplier; baseline = value
    }
}
