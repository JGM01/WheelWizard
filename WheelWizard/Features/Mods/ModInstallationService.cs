using System.Collections.ObjectModel;
using WheelWizard.Core.Mods;
using WheelWizard.Models.Mods;
using WheelWizard.Services;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.Mods;

public interface IModInstallationService
{
    Task<OperationResult<ObservableCollection<Mod>>> LoadModsAsync();

    Task<OperationResult> SaveModsAsync(ObservableCollection<Mod> mods);

    bool ContainsModByTitle(IEnumerable<Mod> mods, string modName);

    Task<OperationResult<Mod>> InstallModFromFileAsync(
        string filePath,
        string givenModName,
        int priority,
        string author = "-1",
        int modID = -1
    );
}

// Preserve the public collection/model API while Core owns archive and metadata work.
public sealed class ModInstallationService : IModInstallationService
{
    private readonly ModLibrary library = new(PathManager.ModsFolderPath);

    public async Task<OperationResult<ObservableCollection<Mod>>> LoadModsAsync()
    {
        try
        {
            return new ObservableCollection<Mod>((await Task.Run(() => library.Load())).Select(Mod.FromMetadata));
        }
        catch (Exception ex)
        {
            return new OperationError { Message = $"Failed to load mod metadata: {ex.Message}", Exception = ex };
        }
    }

    public async Task<OperationResult> SaveModsAsync(ObservableCollection<Mod> mods)
    {
        var snapshot = mods.Select(mod => mod.ToMetadata()).ToArray();
        try
        {
            await Task.Run(() => library.Save(snapshot));
            return Ok();
        }
        catch (Exception ex)
        {
            return new OperationError { Message = $"Failed to save mod metadata: {ex.Message}", Exception = ex };
        }
    }

    public bool ContainsModByTitle(IEnumerable<Mod> mods, string modName) =>
        mods.Any(mod => mod.Title.Equals(modName, StringComparison.OrdinalIgnoreCase));

    public async Task<OperationResult<Mod>> InstallModFromFileAsync(
        string filePath,
        string givenModName,
        int priority,
        string author = "-1",
        int modID = -1
    )
    {
        var window = new ProgressWindow(t("progress.installing_mod")).SetGoal(t("state.extracting"));
        try
        {
            window.Show();
            var progress = new Progress<ModProgress>(update => window.UpdateProgress(update.Percent));
            return Mod.FromMetadata(await Task.Run(() => library.Import(filePath, givenModName, priority, author, modID, progress)));
        }
        catch (Exception ex)
        {
            return new OperationError { Message = $"Failed to install mod: {ex.Message}", Exception = ex };
        }
        finally
        {
            window.Close();
        }
    }
}
