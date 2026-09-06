using WheelWizard.Core.Mods;

namespace WheelWizard.Helpers;

public static class PathSafetyHelper
{
    public static bool TryGetPathWithinDirectory(string directory, string relativePath, out string fullPath) =>
        ModPaths.TryGetPathWithinDirectory(directory, relativePath, out fullPath);

    public static bool IsPathWithinDirectory(string directory, string path) => ModPaths.IsPathWithinDirectory(directory, path);

    public static bool TryNormalizeRelativePath(string path, out string normalizedPath) =>
        ModPaths.TryNormalizeRelativePath(path, out normalizedPath);

    public static bool TryGetSafeFileName(string fileName, out string safeFileName) =>
        ModPaths.TryGetSafeFileName(fileName, out safeFileName);
}
