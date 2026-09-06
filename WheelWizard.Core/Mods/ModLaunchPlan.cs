namespace WheelWizard.Core.Mods;

public sealed record ModFileSource(string ModTitle, string SourcePath);

public sealed record ModLaunchFile(string Destination, ModFileSource Winner, IReadOnlyList<ModFileSource> Overwritten);

public sealed record ModLaunchPlan(IReadOnlyList<ModLaunchFile> Files);

public static class ModLaunchPlanner
{
    public static ModLaunchPlan Build(string root, IReadOnlyList<ModMetadata> mods, CancellationToken ct = default)
    {
        // Build the final file list. The caller's ordered list, not a second priority sort, determines precedence.
        var candidates = new Dictionary<string, List<ModFileSource>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            if (!mod.IsEnabled)
                continue;
            var directory = new ModLibrary(root).DirectoryFor(mod.Title);
            if (!Directory.Exists(directory))
                continue;
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (!ShouldCopyFile(root, mod, file))
                    continue;
                var destination = GetLaunchPatchFileName(mod, file);
                if (!candidates.TryGetValue(destination, out var sources))
                    candidates[destination] = sources = [];
                // Since higher priority mods overwrite lower ones, the last candidate wins.
                // Modding archives keep separate filenames so Pulsar can resolve conflicts inside the archives.
                sources.Add(new(mod.Title, file));
            }
        }
        return new(
            candidates
                .Select(pair => new ModLaunchFile(pair.Key, pair.Value[^1], pair.Value.Take(pair.Value.Count - 1).Reverse().ToArray()))
                .ToArray()
        );
    }

    public static bool ShouldAskToClearTargetFolder(string target, IEnumerable<ModMetadata> mods) =>
        !mods.Any(mod => mod.IsEnabled) && Directory.Exists(target) && Directory.EnumerateFiles(target).Any();

    public static void Prepare(
        string root,
        string target,
        IReadOnlyList<ModMetadata> mods,
        bool clearWhenDisabled = false,
        IProgress<ModProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        if (!mods.Any(mod => mod.IsEnabled))
        {
            if (clearWhenDisabled && ShouldAskToClearTargetFolder(target, mods))
                Directory.Delete(target, true);
            return;
        }
        Copy(target, Build(root, mods, ct), progress, ct);
    }

    public static void Copy(string target, ModLaunchPlan plan, IProgress<ModProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(target);
        var destinations = plan.Files.Select(file => file.Destination).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(target, "*.*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (!destinations.Contains(Path.GetFileName(file)))
                File.Delete(file);
        }
        var processed = 0;
        foreach (var file in plan.Files)
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, file.Destination);
            progress?.Report(new(file.Destination, ++processed * 100 / plan.Files.Count));
            if (File.Exists(destination))
            {
                var sourceInfo = new FileInfo(file.Winner.SourcePath);
                var destInfo = new FileInfo(destination);
                if (sourceInfo.Length == destInfo.Length && sourceInfo.LastWriteTimeUtc == destInfo.LastWriteTimeUtc)
                    continue;
            }
            File.Copy(file.Winner.SourcePath, destination, true);
        }
    }

    private static bool ShouldCopyFile(string root, ModMetadata mod, string filePath)
    {
        var modMetadataFile = Path.Combine(root, mod.Title, $"{mod.Title}.ini");
        if (Path.GetFullPath(filePath).Equals(Path.GetFullPath(modMetadataFile), StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static string GetLaunchPatchFileName(ModMetadata mod, string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (!IsModdingArchiveFile(fileName))
            return fileName;

        return $"{mod.Priority}.{StripExistingPriorityPrefix(fileName)}";
    }

    private static bool IsModdingArchiveFile(string fileName)
    {
        if (!fileName.EndsWith(".szs", StringComparison.OrdinalIgnoreCase))
            return false;

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var tagSeparator = nameWithoutExtension.LastIndexOf('.');
        return tagSeparator > 0 && tagSeparator + 1 < nameWithoutExtension.Length;
    }

    private static string StripExistingPriorityPrefix(string fileName)
    {
        var index = 0;
        while (index < fileName.Length && char.IsDigit(fileName[index]))
            index++;

        return index > 0 && index < fileName.Length && fileName[index] == '.' ? fileName[(index + 1)..] : fileName;
    }
}
