using WheelWizard.Models.Enums;
using WheelWizard.Recomp;
using WheelWizard.Core.Recomp;

namespace WheelWizard.Test.Features.Recomp;

/// <summary>
/// Checks the framework status mapping against setup reports from Core. Command construction,
/// parsing, and payload decisions are covered in WheelWizard.Core.Test.
/// </summary>
public class RecompTests
{
    [Fact]
    public void Status_IsOnlyReadyWhenTheInstallWasActuallyVerified()
    {
        var current = new RecompProductStatus(RecompProductState.Current, "ok");
        var verified = new RecompProductsEvent("0.3.0", @"D:\WiiCompiled", RebuildRequired: false, current, current);

        Assert.Equal(WheelWizardStatus.Ready, RecompStatusResolver.Resolve(true, "0.3.0", "v0.3.0", verified));
        Assert.Equal(WheelWizardStatus.OutOfDate, RecompStatusResolver.Resolve(true, "0.3.0", "v0.4.0", verified));
        Assert.Equal(WheelWizardStatus.NotInstalled, RecompStatusResolver.Resolve(true, null, "v0.3.0", verified));
        Assert.Equal(WheelWizardStatus.ConfigNotFinished, RecompStatusResolver.Resolve(false, "0.3.0", "v0.3.0", verified));

        // A check that never answered must never read as ready.
        Assert.Equal(WheelWizardStatus.OutOfDate, RecompStatusResolver.Resolve(true, "0.3.0", "v0.3.0", products: null));
    }
}
