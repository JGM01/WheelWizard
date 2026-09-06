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
    public static ModPreparation Preparation(string target) => new(target, Path.Combine(PathManager.WheelWizardAppdataPath, "ModTransactions"));

    public bool ShouldAskToClearTargetFolder(string targetFolderPath) =>
        ModLaunchPlanner.ShouldAskToClearTargetFolder(targetFolderPath, modManager.Mods.Select(mod => mod.ToMetadata()));

    public async Task<OperationResult> PrepareModsForLaunch(string targetFolderPath, bool clearTargetFolderWhenNoEnabledMods = false)
    {
        var mods = modManager.Mods.Select(mod => mod.ToMetadata()).ToArray();
        ProgressWindow? window = null;
        try
        {
            var preparation = Preparation(targetFolderPath);
            if (preparation.Recovery is { } recovery)
            {
                var restore = await new YesNoWindow()
                    .SetMainText("Restore previous patches?")
                    .SetExtraText(recovery.Message + "\n" + recovery.RecordPath)
                    .SetButtonText("Restore previous patches", "Cancel")
                    .AwaitAnswer();
                if (!restore) return Fail(recovery.Message);
                await Task.Run(preparation.Restore);
                return Fail("Previous patches restored. Select Play again to review the restored patches before launch.");
            }
            using var cancellation = new CancellationTokenSource();
            window = new ProgressWindow(t("progress.installing_mods"))
                .SetGoal(t("progress.installing_mods"))
                .SetCancellationTokenSource(cancellation);
            window.Show();
            var progress = new Progress<ModProgress>(update =>
            {
                window.UpdateProgress(update.Percent);
                window.SetExtraText($"{t("state.installing")} {update.Stage}");
            });
            await Task.Run(() => preparation.Prepare(PathManager.ModsFolderPath, mods,
                clearTargetFolderWhenNoEnabledMods, progress: progress, ct: cancellation.Token));
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
