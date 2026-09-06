using System.IO.Abstractions;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Semver;
using WheelWizard.Core;
using WheelWizard.Helpers;
using WheelWizard.Models.Enums;
using WheelWizard.Services;
using WheelWizard.Settings;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.CustomDistributions;

// Thin UI adapter over WheelWizard.Core.RetroRewindManager. It owns the frontend concerns -
// where Retro Rewind is installed, the settings gate, save backups and progress windows -
// while the whole download/install/update/delete orchestration lives in Core so the native
// frontend can reuse it through the host helper.
public class RetroRewind : IDistribution
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<IDistribution> _logger;
    private readonly ISettingsManager _settingsManager;

    public RetroRewind(
        IFileSystem fileSystem,
        ILogger<IDistribution> logger,
        ISettingsManager settingsManager
    )
    {
        _fileSystem = fileSystem;
        _logger = logger;
        _settingsManager = settingsManager;
    }

    public string Title => "Retro Rewind";

    // Keep in mind, whenever we download update files from the server, they are actually 1 folder higher, so it contains this folder.
    public string FolderName => "RetroRewind6";
    public string XMLFolderName => "riivolution";
    public string XMLFileName => "RetroRewind6";

    private string Root => PathManager.RiivolutionWhWzFolderPath;

    public async Task<OperationResult> InstallAsync(ProgressWindow progressWindow)
    {
        if (WheelWizard.Mods.ModsLaunchService.Preparation(PathManager.PatchesFolderPath).Recovery is { } recovery)
            return Fail(recovery.Message + " Open Play to restore previous patches. " + recovery.RecordPath);
        if (GetCurrentVersion() is not null)
        {
            var removeResult = await RemoveAsync(progressWindow);
            if (removeResult.IsFailure)
                return removeResult;
        }

        if (HasOldRksys())
        {
            var rksysQuestion = new YesNoWindow()
                .SetMainText(t("question.old_rksys_found.title"))
                .SetExtraText(t("question.old_rksys_found.extra"));
            if (await rksysQuestion.AwaitAnswer())
                await BackupOldrksys();
        }

        return await RunWithProgress(progressWindow, (manager, ct) => manager.InstallAsync(Root, ProgressFor(progressWindow), ct));
    }

    public async Task<OperationResult> UpdateAsync(ProgressWindow progressWindow)
    {
        if (GetCurrentVersion() == null)
            return await InstallAsync(progressWindow);
        return await RunWithProgress(progressWindow, (manager, ct) => manager.UpdateAsync(Root, ProgressFor(progressWindow), ct));
    }

    public async Task<OperationResult> ReinstallAsync(ProgressWindow progressWindow)
    {
        var removeResult = await RemoveAsync(progressWindow);
        return removeResult.IsFailure ? removeResult : await InstallAsync(progressWindow);
    }

    public Task<OperationResult> RemoveAsync(ProgressWindow progressWindow)
    {
        try
        {
            WheelWizard.Mods.ModsLaunchService.Preparation(PathManager.PatchesFolderPath).EnsureReady();
            RetroRewindManager.UninstallAsync(Root);
            return Task.FromResult(Ok());
        }
        catch (Exception e)
        {
            _logger.LogError(e, e.Message);
            OperationResult result = Fail(e);
            return Task.FromResult(result);
        }
    }

    private static IProgress<PackageProgress> ProgressFor(ProgressWindow window) =>
        new Progress<PackageProgress>(p =>
            Dispatcher.UIThread.Post(() =>
            {
                if (p.Percent is { } percent)
                    window.UpdateProgress((int)percent);
                if (p.Stage is { Length: > 0 } stage)
                    window.SetExtraText(stage);
            })
        );

    private async Task<OperationResult> RunWithProgress(
        ProgressWindow window,
        Func<RetroRewindManager, CancellationToken, Task> operation
    )
    {
        using var cancellation = new CancellationTokenSource();
        window.SetCancellationTokenSource(cancellation);
        try
        {
            WheelWizard.Mods.ModsLaunchService.Preparation(PathManager.PatchesFolderPath).EnsureReady();
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            await operation(new RetroRewindManager(http), cancellation.Token);
            return Ok();
        }
        catch (OperationCanceledException)
        {
            return Ok();
        }
        catch (Exception e)
        {
            _logger.LogError(e, e.Message);
            return Fail(e);
        }
        finally
        {
            window.SetCancellationTokenSource(null);
        }
    }

    private async Task BackupOldrksys()
    {
        var rrWfc = GetOldRksys();
        if (!_fileSystem.Directory.Exists(rrWfc))
            return;
        var rksysFiles = _fileSystem.Directory.GetFiles(rrWfc, "rksys.dat", SearchOption.AllDirectories);
        if (rksysFiles.Length == 0)
            return;
        var sourceFile = rksysFiles[0];
        var regionFolder = _fileSystem.Path.GetDirectoryName(sourceFile);
        var regionFolderName = _fileSystem.Path.GetFileName(regionFolder);
        var datFileData = await _fileSystem.File.ReadAllBytesAsync(sourceFile);
        if (regionFolderName == null)
            return;
        var destinationFolder = _fileSystem.Path.Combine(PathManager.SaveFolderPath, regionFolderName);
        _fileSystem.Directory.CreateDirectory(destinationFolder);
        var destinationFile = _fileSystem.Path.Combine(destinationFolder, "rksys.dat");
        await _fileSystem.File.WriteAllBytesAsync(destinationFile, datFileData);
    }

    private bool HasOldRksys()
    {
        return !string.IsNullOrWhiteSpace(GetOldRksys());
    }

    private string GetOldRksys()
    {
        // todo, maybe we should check for the existence of the file instead of the folder? and also find the oldest one?
        var rrWfcPaths = new[]
        {
            PathManager.SaveFolderPath,
            // Also consider the folder with upper-case `Save`
            _fileSystem.Path.Combine(PathManager.RiivolutionWhWzFolderPath, "riivolution", "Save", "RetroWFC"),
            _fileSystem.Path.Combine(PathManager.LoadFolderPath, "Riivolution", "save", "RetroWFC"),
            _fileSystem.Path.Combine(PathManager.LoadFolderPath, "Riivolution", "Save", "RetroWFC"),
            _fileSystem.Path.Combine(PathManager.LoadFolderPath, "riivolution", "save", "RetroWFC"),
            _fileSystem.Path.Combine(PathManager.LoadFolderPath, "riivolution", "Save", "RetroWFC"),
        };

        foreach (var rrWfc in rrWfcPaths)
        {
            if (!_fileSystem.Directory.Exists(rrWfc))
                continue;
            var rksysFiles = _fileSystem.Directory.GetFiles(rrWfc, "rksys.dat", SearchOption.AllDirectories);
            if (rksysFiles.Length > 0)
                return rrWfc;
        }

        return string.Empty;
    }

    public async Task<OperationResult<WheelWizardStatus>> GetCurrentStatusAsync()
    {
        if (!_settingsManager.PathsSetupCorrectly())
            return WheelWizardStatus.ConfigNotFinished;

        var rrInstalled = GetCurrentVersion() != null;

        using var http = new HttpClient();
        var manager = new RetroRewindManager(http);
        bool serverReachable;
        try
        {
            await manager.PingAsync(default);
            serverReachable = true;
        }
        catch (Exception)
        {
            serverReachable = false;
        }

        if (!serverReachable)
            return rrInstalled ? WheelWizardStatus.NoServerButInstalled : WheelWizardStatus.NoServer;

        if (!rrInstalled)
            return WheelWizardStatus.NotInstalled;

        try
        {
            var currentVersion = GetCurrentVersion();
            if (currentVersion == null)
                return WheelWizardStatus.NotInstalled;
            var latestVersion = await manager.LatestVersionAsync(default);
            return currentVersion.ComparePrecedenceTo(latestVersion) >= 0
                ? WheelWizardStatus.Ready
                : WheelWizardStatus.OutOfDate;
        }
        catch (Exception)
        {
            return Fail("Failed to check for updates");
        }
    }

    public SemVersion? GetCurrentVersion()
    {
        var version = RetroRewindManager.InstalledVersion(Root);
        return version != null && SemVersion.TryParse(version, out var parsed) ? parsed : null;
    }
}
