using WheelWizard.Core.Recomp;

namespace WheelWizard.Core.Test;

/// <summary>
/// Smoke coverage for the setup command line, reports, and payload decisions. These contracts are
/// frontend-agnostic and run the same everywhere; framework status mapping is tested separately.
/// </summary>
public class RecompContractTests
{
    [Fact]
    public void SilentInstall_BuildsTheCommandLineTheSetupExecutableExpects()
    {
        var arguments = RecompSetupCommandBuilder.BuildSilentInstallArguments(
            new()
            {
                GameFilePath = @"D:\Games\Mario Kart Wii.rvz",
                InstallFolderPath = @"D:\WheelWizard\Recomp\Install",
                RetroRewindFolderPath = @"D:\WheelWizard\RetroRewind6",
                Portable = true,
            }
        );

        Assert.Equal(
            "--silent --game \"D:\\Games\\Mario Kart Wii.rvz\" --install-dir \"D:\\WheelWizard\\Recomp\\Install\" --portable "
                + "--progress-json --retro-dir \"D:\\WheelWizard\\RetroRewind6\" --download-retro-wfc-payload",
            arguments
        );
    }

    [Fact]
    public void SilentInstall_SkipsThePayloadOnlyWhenAskedTo()
    {
        var arguments = RecompSetupCommandBuilder.BuildSilentInstallArguments(
            new()
            {
                GameFilePath = @"D:\Games\Mario Kart Wii.rvz",
                InstallFolderPath = @"D:\WheelWizard\Recomp\Install",
                RetroRewindFolderPath = @"D:\WheelWizard\RetroRewind6",
                RetroWfcPayloadMode = RecompRetroWfcPayloadMode.Skip,
            }
        );

        Assert.EndsWith("--retro-dir \"D:\\WheelWizard\\RetroRewind6\" --skip-retro-wfc-payload", arguments);
        Assert.DoesNotContain("--download-retro-wfc-payload", arguments);
    }

    [Fact]
    public void RepairProducts_PassesExactlyOnePayloadOption()
    {
        Assert.Equal(
            "--repair-products --install-dir \"D:\\Recomp\" --retro-dir \"D:\\RetroRewind6\" --download-retro-wfc-payload --progress-json",
            RecompSetupCommandBuilder.BuildRepairProductsArguments(@"D:\Recomp", @"D:\RetroRewind6")
        );
        Assert.Equal(
            "--repair-products --install-dir \"D:\\Recomp\" --retro-dir \"D:\\RetroRewind6\" --skip-retro-wfc-payload --progress-json",
            RecompSetupCommandBuilder.BuildRepairProductsArguments(@"D:\Recomp", @"D:\RetroRewind6", RecompRetroWfcPayloadMode.Skip)
        );
    }

    [Fact]
    public void InstallState_ReadsThePayloadModeTheSetupHostWrites()
    {
        var state = System.Text.Json.JsonSerializer.Deserialize<RecompInstallState>(
            """{"SchemaVersion":1,"SetupVersion":"0.3.0","InstallDir":"D:\\Recomp","RetroWfcPayloadMode":"skipped"}""",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        Assert.NotNull(state);
        Assert.True(state.IsRetroWfcPayloadSkipped);
    }

    [Fact]
    public void PayloadPolicy_LegacyStateWithoutModeKeepsDownloadingWhenTheServiceIsDown()
    {
        // Written by a host that predates the mode field: Retro Rewind is installed, so the host owns a
        // verified payload copy and must be asked to download, never to skip or to bother the user.
        var legacy = new RecompInstallState
        {
            SchemaVersion = 1,
            SetupVersion = "0.2.25",
            InstallDir = @"D:\Recomp",
            RetroRewindInstalled = true,
        };

        Assert.False(RecompRetroWfcPayloadPolicy.NeedsServiceProbe(legacy, hasRetroRewindSource: true));
        Assert.Equal(RecompRetroWfcPayloadDecision.Download, RecompRetroWfcPayloadPolicy.Decide(legacy, true, serviceReachable: false));
    }

    [Fact]
    public void PayloadPolicy_OnlyAFreshRetroRewindBuildAsksTheUser()
    {
        var downloaded = new RecompInstallState { RetroWfcPayloadMode = "downloaded", RetroRewindInstalled = true };
        var skipped = new RecompInstallState { RetroWfcPayloadMode = "skipped", RetroRewindInstalled = true };
        var baseOnly = new RecompInstallState { RetroWfcPayloadMode = "", RetroRewindInstalled = false };

        // Nothing to decide without a Retro Rewind source, and never a probe for a payload-bearing install.
        Assert.False(RecompRetroWfcPayloadPolicy.NeedsServiceProbe(null, hasRetroRewindSource: false));
        Assert.False(RecompRetroWfcPayloadPolicy.NeedsServiceProbe(downloaded, hasRetroRewindSource: true));
        Assert.Equal(RecompRetroWfcPayloadDecision.Download, RecompRetroWfcPayloadPolicy.Decide(downloaded, true, serviceReachable: false));

        // A skipped install stays offline while the service is down and upgrades when it is back.
        Assert.True(RecompRetroWfcPayloadPolicy.NeedsServiceProbe(skipped, hasRetroRewindSource: true));
        Assert.Equal(RecompRetroWfcPayloadDecision.Skip, RecompRetroWfcPayloadPolicy.Decide(skipped, true, serviceReachable: false));
        Assert.Equal(RecompRetroWfcPayloadDecision.Download, RecompRetroWfcPayloadPolicy.Decide(skipped, true, serviceReachable: true));

        // A fresh install and a base-only install both need a brand new Retro Rewind build.
        Assert.Equal(RecompRetroWfcPayloadDecision.AskUser, RecompRetroWfcPayloadPolicy.Decide(null, true, serviceReachable: false));
        Assert.Equal(RecompRetroWfcPayloadDecision.AskUser, RecompRetroWfcPayloadPolicy.Decide(baseOnly, true, serviceReachable: false));
        Assert.Equal(RecompRetroWfcPayloadDecision.Download, RecompRetroWfcPayloadPolicy.Decide(null, true, serviceReachable: true));
    }

    [Fact]
    public void ProductsLine_IsReadBackAsTheProductStateItReports()
    {
        var parsed = RecompSetupOutputParser.Parse(
            """
            {"type":"products","setupVersion":"0.3.0","installDir":"D:\\WiiCompiled","rebuildRequired":false,"base":{"status":"current","detail":"ok"},"retroRewind":{"status":"code-pul-changed","detail":"Code.pul changed"}}
            """
        );

        var products = Assert.IsType<RecompProductsEvent>(parsed);
        Assert.Equal("0.3.0", products.SetupVersion);
        Assert.True(products.Base.IsCurrent);
        Assert.Equal(RecompProductState.CodePulChanged, products.RetroRewind.State);
        Assert.True(products.ActionRequired);
    }

}
