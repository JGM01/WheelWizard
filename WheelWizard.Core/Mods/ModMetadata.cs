using IniParser;
using IniParser.Model;

namespace WheelWizard.Core.Mods;

public sealed record ModMetadata(string Title, string Author = "-1", int ModID = -1, bool IsEnabled = true, int Priority = 0);

public sealed record ModProgress(string Stage, int Percent);

public static class ModMetadataFile
{
    /// <summary>Loads the mod details from an INI file.</summary>
    public static ModMetadata Load(string iniFilePath)
    {
        IniData data;
        try
        {
            data = new FileIniDataParser().ReadFile(iniFilePath);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to read INI file '{iniFilePath}': {ex.Message}", ex);
        }
        return new(
            data["Mod"]["Name"] ?? string.Empty,
            data["Mod"]["Author"] ?? string.Empty,
            int.TryParse(data["Mod"]["ModID"], out var id) ? id : -1,
            !bool.TryParse(data["Mod"]["IsEnabled"], out var enabled) || enabled,
            int.TryParse(data["Mod"]["Priority"], out var priority) ? priority : 0
        );
    }

    /// <summary>Saves the mod details to an INI file.</summary>
    public static void Save(string iniFilePath, ModMetadata mod)
    {
        var data = new IniData();
        data["Mod"]["Name"] = mod.Title;
        data["Mod"]["Author"] = mod.Author;
        data["Mod"]["ModID"] = mod.ModID.ToString();
        data["Mod"]["IsEnabled"] = mod.IsEnabled.ToString();
        data["Mod"]["Priority"] = mod.Priority.ToString();
        try
        {
            new FileIniDataParser().WriteFile(iniFilePath, data);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to write INI file '{iniFilePath}': {ex.Message}", ex);
        }
    }
}
