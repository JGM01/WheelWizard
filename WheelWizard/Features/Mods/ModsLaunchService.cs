using WheelWizard.Core.Mods;
using WheelWizard.Services;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.Mods;

public interface IModsLaunchService
{
    bool ShouldAskToClearTargetFolder(string targetFolderPath);
    Task<OperationResult> PrepareModsForLaunch(string targetFolderPath, bool clearTargetFolderWhenNoEnabledMods = false);
}

// The framework owns the bound list, progress window, and clear-target confirmation.
public sealed class ModsLaunchService(IModManager modManager) : IModsLaunchService
{
    public bool ShouldAskToClearTargetFolder(string targetFolderPath) =>
        ModLaunchPlanner.ShouldAskToClearTargetFolder(targetFolderPath, modManager.Mods.Select(mod => mod.ToMetadata()));

    public async Task<OperationResult> PrepareModsForLaunch(string targetFolderPath, bool clearTargetFolderWhenNoEnabledMods = false)
    {
        var mods = modManager.Mods.Select(mod => mod.ToMetadata()).ToArray();
        ProgressWindow? window = null;
        try
        {
            var plan = mods.Any(mod => mod.IsEnabled)
                ? await Task.Run(() => ModLaunchPlanner.Build(PathManager.ModsFolderPath, mods))
                : null;
            if (plan is not null)
            {
                window = new ProgressWindow(t("progress.installing_mods")).SetGoal(t("progress.installing_mods_count", plan.Files.Count)!);
                window.Show();
            }
            var progress = new Progress<ModProgress>(update =>
            {
                window?.UpdateProgress(update.Percent);
                window?.SetExtraText($"{t("state.installing")} {update.Stage}");
            });
            await Task.Run(() =>
            {
                if (plan is null)
                    ModLaunchPlanner.Prepare(
                        PathManager.ModsFolderPath,
                        targetFolderPath,
                        mods,
                        clearTargetFolderWhenNoEnabledMods,
                        progress
                    );
                else
                    ModLaunchPlanner.Copy(targetFolderPath, plan, progress);
            });
            return Ok();
        }
        catch (Exception ex)
        {
            return new OperationError { Message = $"Failed to prepare mods for launch: {ex.Message}", Exception = ex };
        }
        finally
        {
            window?.Close();
        }
    }
}
