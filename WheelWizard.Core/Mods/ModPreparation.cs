using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WheelWizard.Core.Mods;

public sealed record PatchRecovery(string Target, string RecordPath, string Message);

// The journal lives outside the package, while staging and backup stay on the target filesystem.
// A journal without a completion marker always requires explicit recovery after process death.
public sealed class ModPreparation
{
    public string Target { get; }
    public string RecordPath { get; }
    private readonly string parent;
    private sealed record Transaction(string Stage, string Backup, bool HadTarget, string Phase);

    public ModPreparation(string target, string transactions)
    {
        Target = Path.GetFullPath(target);
        parent = Path.GetDirectoryName(Target)!;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Target)));
        RecordPath = Path.Combine(Path.GetFullPath(transactions), key + ".json");
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
        return new FileStream(RecordPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public PatchRecovery? Recovery
    {
        get
        {
            if (!File.Exists(RecordPath)) return null;
            try
            {
                if (Read().Phase is "committed" or "restored") return null;
            }
            catch (Exception) { /* A damaged journal must not be mistaken for a ready target. */ }
            return new(Target, RecordPath, "Patch publication was interrupted. Restore previous patches before changing or launching Retro Rewind.");
        }
    }

    public void EnsureReady()
    {
        if (File.Exists(Target)) throw new IOException("The patch directory is occupied by a file: " + Target);
        if (Recovery is { } recovery)
            throw new InvalidOperationException(recovery.Message + " Recovery record: " + RecordPath);
    }

    private Transaction Read()
    {
        var tx = JsonSerializer.Deserialize<Transaction>(File.ReadAllText(RecordPath)) ?? throw new IOException("Invalid patch transaction");
        foreach (var path in new[] { tx.Stage, tx.Backup })
            if (Path.GetDirectoryName(path) != parent || !Path.GetFileName(path).StartsWith(".ww-patches-", StringComparison.Ordinal))
                throw new IOException("Patch transaction contains an unexpected path: " + RecordPath);
        if (tx.Stage == tx.Backup || tx.Phase is not ("publishing" or "committed" or "restored"))
            throw new IOException("Invalid patch transaction: " + RecordPath);
        return tx;
    }

    private void Write(Transaction tx)
    {
        var temporary = RecordPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, tx);
            stream.Flush(true);
        }
        File.Move(temporary, RecordPath, true);
    }

    private static void Remove(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private void Cleanup(Transaction tx)
    {
        Remove(tx.Stage);
        Remove(tx.Backup);
        Remove(tx.Stage + "-restore");
        File.Delete(RecordPath);
    }

    public void Restore()
    {
        using var gate = Lock();
        if (!File.Exists(RecordPath)) return;
        var tx = Read();
        if (tx.Phase is "committed" or "restored") { Cleanup(tx); return; }
        Restore(tx);
    }

    private void Restore(Transaction tx)
    {
        if (tx.HadTarget)
        {
            if (Directory.Exists(tx.Backup))
            {
                // Keep the backup intact until restoration is durably recorded. Retrying a failed
                // restoration, including a second process death, is therefore safe.
                var restored = tx.Stage + "-restore";
                Remove(restored);
                CopyTree(tx.Backup, restored, CancellationToken.None);
                Remove(Target);
                Directory.Move(restored, Target);
            }
            else if (!Directory.Exists(tx.Stage) || !Directory.Exists(Target))
                throw new IOException("Cannot identify the previous patch set. Preserve files and inspect " + RecordPath);
            // Stage and target both present with no backup means the first rename never happened.
        }
        else Remove(Target);
        tx = tx with { Phase = "restored" };
        Write(tx);
        Cleanup(tx);
    }

    public void Prepare(string root, IReadOnlyList<ModMetadata> mods, bool clearWhenDisabled,
        bool requireCompatibility = false, IProgress<ModProgress>? progress = null,
        Action<string>? phase = null, CancellationToken ct = default)
    {
        using var gate = Lock();
        EnsureReady();
        if (File.Exists(RecordPath)) Cleanup(Read());
        ct.ThrowIfCancellationRequested();
        var enabled = mods.Where(mod => mod.IsEnabled).ToArray();
        var library = new ModLibrary(root);
        var findings = new List<ModCompatibilityFinding>();
        foreach (var mod in enabled)
        {
            var directory = library.DirectoryFor(mod.Title);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Enabled mod is missing: " + mod.Title);
            if (requireCompatibility) findings.AddRange(ModCompatibility.Scan(directory, mod.Title, ct));
        }
        if (enabled.Length == 0 && !clearWhenDisabled)
        {
            if (requireCompatibility && Directory.Exists(Target)) findings.AddRange(ModCompatibility.Scan(Target, ct: ct));
            if (findings.Count > 0) throw new ModCompatibilityException(findings);
            return;
        }
        if (findings.Count > 0) throw new ModCompatibilityException(findings);

        phase?.Invoke("preparing");
        var plan = ModLaunchPlanner.Build(root, mods, ct);
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".ww-patches-" + Guid.NewGuid().ToString("N"));
        var tx = new Transaction(stage, stage + "-previous", Directory.Exists(Target), "publishing");
        try
        {
            // Retain the old copier's subdirectory behavior; only its top-level planned files
            // and cleanup rules change the staged tree. Delete explicitly creates an empty set.
            if (enabled.Length > 0 && tx.HadTarget) CopyTree(Target, stage, ct);
            else Directory.CreateDirectory(stage);
            ModLaunchPlanner.Copy(stage, plan, progress, ct);
            foreach (var file in plan.Files)
                if (!File.Exists(Path.Combine(stage, file.Destination))) throw new IOException("Missing staged patch: " + file.Destination);
            ct.ThrowIfCancellationRequested();
            phase?.Invoke("publishing");
            Write(tx);
            try
            {
                // No cancellation in this commit/rollback barrier. A late cancellation prevents
                // game launch, but must never leave a half-published target.
                if (tx.HadTarget) Directory.Move(Target, tx.Backup);
                Directory.Move(stage, Target);
                Write(tx with { Phase = "committed" });
            }
            catch (Exception publicationError)
            {
                try { Restore(tx); }
                catch (Exception restoreError)
                {
                    throw new AggregateException("Patch recovery required: " + RecordPath, publicationError, restoreError);
                }
                throw;
            }
            // Committed content is valid even when obsolete backup cleanup fails.
            try { Cleanup(tx); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            if (!File.Exists(RecordPath)) Remove(stage);
        }
    }

    private static void CopyTree(string source, string destination, CancellationToken ct)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked patch directories are not supported: " + source);
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked patch files are not supported: " + entry);
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) CopyTree(entry, target, ct);
            else File.Copy(entry, target);
        }
    }
}
