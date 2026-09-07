namespace WheelWizard.Core.Archives;

internal static class ArchivePath
{
    // Archive paths are portable relative paths, regardless of the host operating system.
    public static void Validate(string path)
    {
        // Nintendo U8 archives and the embedded baselines use a single leading ./ root.
        var relative = path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
        if (string.IsNullOrWhiteSpace(relative) || path.Contains('\\') || path.Contains(':') || path.Contains('\0')
            || relative.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment.Trim() is "." or ".."))
            throw new InvalidDataException($"Unsafe archive member path '{path}'.");
    }

    public static void ValidateMember(string name)
    {
        Validate(name);
        if (name.Contains('/'))
            throw new InvalidDataException($"Archive member name '{name}' contains a path separator.");
    }
}
