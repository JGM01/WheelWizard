using Testably.Abstractions.Testing;
using WheelWizard.Services;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;

namespace WheelWizard.Test.Features.Settings;

public class RecompSharedServiceTests
{
    [Fact]
    public void ManagerPreservesOtherKeysAndReportsMalformedValues()
    {
        var fs = new MockFileSystem();
        var path = PathManager.RecompConfigFilePath;
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
        fs.File.WriteAllText(path, "[video]\nresolution_multiplier = broken\nunknown = 42\n");
        var manager = new RecompSettingManager(fs);
        var setting = new RecompSetting(typeof(double), ("video", "resolution_multiplier"), 1.0, manager.SaveSettings);
        manager.RegisterSetting(setting);
        manager.LoadSettings();
        Assert.Single(manager.Errors);
        Assert.Equal(1.0, setting.Get());
        Assert.Contains("broken", fs.File.ReadAllText(path));
        fs.File.WriteAllText(path, "[video]\nresolution_multiplier = 1.5\nunknown = 42\n");
        manager.ReloadSettings();
        Assert.Empty(manager.Errors);
        Assert.Equal(1.5, setting.Get());
        setting.Set(2.0);
        Assert.Contains("resolution_multiplier = 2.0", fs.File.ReadAllText(path));
        Assert.Contains("unknown = 42", fs.File.ReadAllText(path));
    }

    [Fact]
    public void SettingUsesSharedEscapingAndRejectsMalformedString()
    {
        var setting = new RecompSetting(typeof(string), ("paths", "dvd_root"), "C:\\a\"b", _ => { });
        Assert.Equal(WheelWizard.Core.RuntimeConfiguration.Format("C:\\a\"b"), setting.GetStringValue());
        Assert.False(setting.SetFromString("unquoted"));
        Assert.True(setting.SetFromString("'literal path'"));
        Assert.Equal("literal path", setting.Get());
    }
}
