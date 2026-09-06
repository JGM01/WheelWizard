using Semver;

namespace WheelWizard.Core;

public sealed record UpdateEntry(SemVersion Version, string Url, string Description);

public sealed record DeletionEntry(SemVersion Version, string Path);

// A snapshot of the installed RR package and the latest the server offers, so callers can
// render Install/Update/Ready states without owning the version comparison. ServerReachable
// is false when the latest-version fetch fails (null Latest, not OutOfDate).
public sealed record PackageState(
    string? Version,
    string? Latest,
    bool Installed,
    bool OutOfDate,
    bool ServerReachable
);

// Orchestrates the full Retro Rewind lifecycle against an install root (the directory that
// holds RetroRewind6/ and riivolution/), reusing RetroRewindPackage for the full-package
// download/extract/validation primitives. Every method is frontend-agnostic; callers own
// their root resolution, UI progress and confirmation flows. The server's update format is
// cumulative: RetroRewindVersion.txt lists one "<version> <url> <path> <description>" line
// per release, and RetroRewindDelete.txt lists "<version> <path>" entries that must be
// removed when updating across that version.
public sealed class RetroRewindManager(HttpClient http)
{
    public const string Host = "https://update.rwfc.net/";
    const string VersionFile = "RetroRewindVersion.txt";
    const string DeleteFile = "RetroRewindDelete.txt";

    // Versions older than this cannot be updated incrementally and need a full reinstall.
    static readonly SemVersion FullReinstallBelow = new(3, 2, 6);

    /// <summary>The normalized installed version under <c>root/RetroRewind6/version.txt</c>, or null.</summary>
    public static string? InstalledVersion(string root)
    {
        var file = Path.Combine(root, "RetroRewind6", "version.txt");
        return File.Exists(file) ? RetroRewindPackage.InstalledVersion(File.ReadAllText(file)) : null;
    }

    public async Task PingAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(Host), ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<SemVersion> LatestVersionAsync(CancellationToken ct) =>
        SemVersion.Parse(RetroRewindPackage.LatestVersion(await http.GetStringAsync(RetroRewindPackage.Endpoint + VersionFile, ct)));

    public async Task<bool> HasUpdateAsync(string root, CancellationToken ct)
    {
        var current = Installed(root);
        if (current == null)
            return false;
        return current.ComparePrecedenceTo(await LatestVersionAsync(ct)) < 0;
    }

    public async Task<PackageState> StateAsync(string root, CancellationToken ct)
    {
        var installedText = InstalledVersion(root);
        var installed = Installed(root);
        SemVersion? latest = null;
        bool serverReachable = true;
        try
        {
            latest = await LatestVersionAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Any fetch/parse failure means we cannot vouch for an update right now.
            serverReachable = false;
        }
        return new PackageState(
            installedText,
            latest?.ToString(),
            installed != null,
            installed != null && latest != null && installed.ComparePrecedenceTo(latest) < 0,
            serverReachable
        );
    }

    static SemVersion? Installed(string root) =>
        InstalledVersion(root) is { } version && SemVersion.TryParse(version, out var parsed) ? parsed : null;

    // A full install replaces the RR-owned entries with the freshly staged package. Callers
    // decide whether an existing install must be removed first (the host refuses it, the
    // desktop frontend removes before installing).
    public async Task InstallAsync(string root, IProgress<PackageProgress>? progress, CancellationToken ct)
    {
        var stage = Path.Combine(Path.GetTempPath(), "ww-rr-" + Guid.NewGuid().ToString("N"));
        try
        {
            var staged = await new RetroRewindPackage(http).StageAsync(stage, progress, ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var content = Path.Combine(staged.Path, "content");
                MergeDirectory(Path.Combine(content, "RetroRewind6"), Path.Combine(root, "RetroRewind6"));
                MergeDirectory(Path.Combine(content, "riivolution"), Path.Combine(root, "riivolution"));
            }
            finally
            {
                Directory.Delete(staged.Path, true);
            }
        }
        finally
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, true);
        }
    }

    // Keeps the two RR-owned folders intact when the root is shared with other content (mods).
    static void MergeDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
            return;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    public async Task UpdateAsync(string root, IProgress<PackageProgress>? progress, CancellationToken ct)
    {
        var current = Installed(root);
        if (current == null)
        {
            await InstallAsync(root, progress, ct);
            return;
        }

        var latest = await LatestVersionAsync(ct);
        if (current.ComparePrecedenceTo(latest) >= 0)
            return;

        // Pre-3.2.6 installs predate the incremental update format and need a full reinstall.
        if (current.ComparePrecedenceTo(FullReinstallBelow) < 0)
        {
            UninstallAsync(root);
            await InstallAsync(root, progress, ct);
            return;
        }

        var updates = UpdatesAfter(current, await VersionEntriesAsync(ct));
        if (updates.Count == 0)
            return;

        foreach (var deletion in DeletionsBetween(current, latest, await DeleteEntriesAsync(ct)))
        {
            if (ResolveWithin(root, deletion.Path) is not { } path)
                throw new InvalidDataException("Invalid file path detected. Please contact the developers.\n Server error: " + deletion.Path);
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }

        for (int i = 0; i < updates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var update = updates[i];
            var zip = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                progress?.Report(new PackageProgress(update.Description, null));
                using (var response = await http.GetAsync(Resolve(update.Url), HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    await using (var output = File.Create(zip))
                        await input.CopyToAsync(output, ct);
                }
                RetroRewindPackage.Extract(zip, root, progress, ct);
                WriteVersion(root, update.Version);
            }
            finally
            {
                if (File.Exists(zip))
                    File.Delete(zip);
            }
        }
    }

    /// <summary>Removes only the RR-owned entries, leaving any unrelated root content untouched.</summary>
    public static void UninstallAsync(string root)
    {
        var distributionFolder = Path.Combine(root, "RetroRewind6");
        if (Directory.Exists(distributionFolder))
            Directory.Delete(distributionFolder, recursive: true);
        var discXml = Path.Combine(root, "riivolution", "RetroRewind6.xml");
        if (File.Exists(discXml))
            File.Delete(discXml);
    }

    static Uri Resolve(string url) => RetroRewindPackage.ResolveUrl(url);

    public async Task<IReadOnlyList<UpdateEntry>> VersionEntriesAsync(CancellationToken ct) =>
        ParseVersionFile(await http.GetStringAsync(RetroRewindPackage.Endpoint + VersionFile, ct));

    public async Task<IReadOnlyList<DeletionEntry>> DeleteEntriesAsync(CancellationToken ct) =>
        ParseDeletionFile(await http.GetStringAsync(RetroRewindPackage.Endpoint + DeleteFile, ct));

    public static List<UpdateEntry> ParseVersionFile(string text)
    {
        var entries = new List<UpdateEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 4);
            if (parts.Length < 4)
                continue;
            if (!SemVersion.TryParse(parts[0].Trim(), out var version))
                continue;
            entries.Add(new(version, parts[1].Trim(), parts[3].Trim()));
        }
        return entries;
    }

    public static List<DeletionEntry> ParseDeletionFile(string text)
    {
        var entries = new List<DeletionEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', 2);
            if (parts.Length < 2)
                continue;
            if (!SemVersion.TryParse(parts[0].Trim(), out var version))
                continue;
            var path = parts[1].Trim();
            if (path.Length == 0)
                continue;
            entries.Add(new(version, path));
        }
        return entries;
    }

    public static List<UpdateEntry> UpdatesAfter(SemVersion current, IEnumerable<UpdateEntry> all) =>
        all.Where(u => u.Version.ComparePrecedenceTo(current) > 0).ToList();

    public static List<DeletionEntry> DeletionsBetween(SemVersion current, SemVersion target, IEnumerable<DeletionEntry> all) =>
        all
            .Where(d => d.Version.ComparePrecedenceTo(current) > 0 && d.Version.ComparePrecedenceTo(target) <= 0)
            .ToList();

    // The deletion list is server-controlled, so keep every resolved path inside the install root.
    public static string? ResolveWithin(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var cleaned = path.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        if (cleaned.Length == 0)
            return null;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, cleaned));
        return candidate.StartsWith(fullRoot, StringComparison.Ordinal) ? candidate : null;
    }

    static void WriteVersion(string root, SemVersion version)
    {
        var versionFile = Path.Combine(root, "RetroRewind6", "version.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(versionFile)!);
        File.WriteAllText(versionFile, version.ToString());
    }
}
