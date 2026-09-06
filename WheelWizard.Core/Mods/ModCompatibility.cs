namespace WheelWizard.Core.Mods;

public sealed record ModCompatibilityFinding(string ModTitle, string RelativePath, string Reason);

public sealed class ModCompatibilityException(IReadOnlyList<ModCompatibilityFinding> findings)
    : InvalidOperationException("Disable or convert these files before playing:\n" + string.Join("\n", findings.Select(f => $"{f.ModTitle}: {f.RelativePath} — {f.Reason}")))
{
    public IReadOnlyList<ModCompatibilityFinding> Findings { get; } = findings;
}

// These are the framework's conversion-discovery rules, not a guarantee that arbitrary mods work.
public static class ModCompatibility
{
    public static IReadOnlyList<ModCompatibilityFinding> Scan(string directory, string title = "Existing patches", CancellationToken ct = default)
    {
        var findings = new List<ModCompatibilityFinding>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var reason = ConversionReason(Path.GetFileName(file));
            if (reason != null)
                findings.Add(new(title, Path.GetRelativePath(directory, file), reason));
        }
        return findings;
    }

    public static string? ConversionReason(string fileName)
    {
        if (LooseBrsarPatchFileName.TryGetNormalizedFileName(fileName, out _))
            return "Loose sound patch needs bundling";
        if (fileName.Equals("revo_kart.brsar", StringComparison.OrdinalIgnoreCase))
            return "Sound archive needs conversion";
        return Path.GetExtension(fileName).Equals(".szs", StringComparison.OrdinalIgnoreCase)
            && !IsModdingArchiveFile(fileName) && !KartSzsAllowList.IsAllowedFullCharacterOrKart(fileName)
            ? "Archive needs conversion" : null;
    }

    public static bool IsModdingArchiveFile(string fileName)
    {
        if (!fileName.EndsWith(".szs", StringComparison.OrdinalIgnoreCase))
            return false;
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var tagSeparator = nameWithoutExtension.LastIndexOf('.');
        return tagSeparator > 0 && tagSeparator + 1 < nameWithoutExtension.Length;
    }
}
