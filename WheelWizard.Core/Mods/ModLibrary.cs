using SharpCompress.Archives;

namespace WheelWizard.Core.Mods;

// Paths and storage belong to the caller; no UI, global paths, or observable collections live here.
public sealed class ModLibrary(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Mod name cannot be empty.");
        if (name != name.Trim() || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['.', '/', '~', '\\']) >= 0)
            throw new ArgumentException("Mod name contains illegal characters.");
    }

    public string DirectoryFor(string title)
    {
        ValidateName(title);
        var path = Path.Combine(Root, title);
        // Managed directories must not redirect operations outside the library.
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Mod directory cannot be a symbolic link.");
        return path;
    }

    public List<ModMetadata> Load(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Root);
        var mods = new List<ModMetadata>();
        foreach (var ini in Directory.GetFiles(Root, "*.ini", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var mod = ModMetadataFile.Load(ini);
            if (!string.IsNullOrWhiteSpace(mod.Title))
                mods.Add(mod);
        }
        return mods.OrderBy(m => m.Priority).ToList();
    }

    public void Save(IEnumerable<ModMetadata> mods)
    {
        foreach (var mod in mods)
        {
            var directory = DirectoryFor(mod.Title);
            Directory.CreateDirectory(directory);
            ModMetadataFile.Save(Path.Combine(directory, mod.Title + ".ini"), mod);
        }
    }

    public async Task<ModMetadata> Import(
        string archivePath,
        string title,
        int priority,
        string author = "-1",
        int modID = -1,
        IProgress<ModProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        ValidateName(title);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("File not found.", archivePath);
        var extension = Path.GetExtension(archivePath).ToLowerInvariant();
        if (extension is not (".zip" or ".7z" or ".rar"))
            throw new ArgumentException($"Unsupported file type: {extension}. Only .zip, .7z, and .rar files are supported.");
        Directory.CreateDirectory(Root);
        var target = DirectoryFor(title);
        if (
            Load(ct).Any(m => string.Equals(m.Title, title, StringComparison.OrdinalIgnoreCase))
            || Directory
                .EnumerateFileSystemEntries(Root)
                .Any(p => string.Equals(Path.GetFileName(p), title, StringComparison.OrdinalIgnoreCase))
        )
            throw new IOException($"Mod with name '{title}' already exists.");

        // Staging is a sibling of the library, outside its recursive metadata scan and on the same volume.
        var stage = Path.Combine(Path.GetDirectoryName(Root)!, ".ww-mod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var entries = archive.Entries.Where(entry => !entry.IsDirectory).ToList();
            var processed = 0;
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var key = entry.Key ?? string.Empty;
                if (!ModPaths.TryGetPathWithinDirectory(stage, key, out var path))
                    throw new IOException("Archive entry is outside of the destination directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = entry.OpenEntryStream();
                using var output = File.Create(path);
                await input.CopyToAsync(output, ct);
                progress?.Report(new("Extracting " + key, ++processed * 100 / entries.Count));
            }
            var mod = new ModMetadata(title, author, modID, true, priority);
            ModMetadataFile.Save(Path.Combine(stage, title + ".ini"), mod);
            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            return mod;
        }
        finally
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, true);
        }
    }

    public Task<ModMetadata> ImportNext(
        string archivePath,
        string title,
        string author = "-1",
        int modID = -1,
        IProgress<ModProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var mods = Load(ct);
        var priority = mods.Count == 0 ? 1 : checked(mods.Max(mod => mod.Priority) + 1);
        return Import(archivePath, title, priority, author, modID, progress, ct);
    }

    public void SetEnabled(string title, bool enabled)
    {
        var mod = Find(Load(), title);
        Save([mod with { IsEnabled = enabled }]);
    }

    public void Move(string title, int direction)
    {
        if (direction is not (-1 or 1))
            throw new ArgumentException("Direction must be -1 or 1.");
        var mods = Load();
        var mod = Find(mods, title);
        var index = mods.IndexOf(mod);
        var destination = index + direction;
        if (destination < 0 || destination >= mods.Count)
            return;
        (mods[index], mods[destination]) = (mods[destination], mods[index]);
        // Keep the same contiguous priority convention as the framework's list-move operation.
        Save(mods.Select((m, i) => m with { Priority = i }));
    }

    public void Remove(string title)
    {
        Find(Load(), title);
        DeleteDirectory(Root, DirectoryFor(title));
    }

    /// <summary>Reorders the whole library to the caller-supplied title order, writing contiguous priorities.
    /// The set must match the current titles exactly; nothing is written otherwise.</summary>
    public void Reorder(IEnumerable<string> titles)
    {
        var order = titles.ToList();
        if (order.Count == 0)
            throw new ArgumentException("Reorder requires at least one mod.");
        var mods = Load();
        var lookup = mods.ToDictionary(m => m.Title);
        if (order.Count != mods.Count || order.Any(title => !lookup.ContainsKey(title)))
            throw new ArgumentException("Reorder must contain exactly the current mod titles.");
        Save(order.Select((title, index) => lookup[title] with { Priority = index }));
    }

    static ModMetadata Find(List<ModMetadata> mods, string title) =>
        mods.SingleOrDefault(m => m.Title == title) ?? throw new ArgumentException("Cannot find mod.");

    public static void DeleteDirectory(string root, string target)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(target));
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new ArgumentException("Invalid mod directory.");
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException("Mod directory does not exist.");
        var directory = new DirectoryInfo(target);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Mod directory cannot be a symbolic link.");
        foreach (
            var child in directory.EnumerateFileSystemInfos(
                "*",
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }
            )
        )
            child.Attributes &= ~FileAttributes.ReadOnly;
        directory.Attributes &= ~FileAttributes.ReadOnly;
        Directory.Delete(target, true);
    }
}
