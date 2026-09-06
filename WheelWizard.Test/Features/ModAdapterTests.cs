using WheelWizard.Core.Mods;
using WheelWizard.Models.Mods;

namespace WheelWizard.Test.Features;

public class ModAdapterTests
{
    [Fact]
    public async Task AdapterPreservesNotificationsAndMetadataApi()
    {
        var mod = Mod.FromMetadata(new("Example", "Author", 7, true, 2));
        var notifications = new List<string?>();
        mod.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        mod.IsEnabled = false;
        mod.IsEnabled = false;
        mod.Priority = 3;
        Assert.Equal(new[] { "IsEnabled", "Priority" }, notifications);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini");
        try
        {
            await mod.SaveToIniAsync(path);
            Assert.Equal(mod.ToMetadata(), (await Mod.LoadFromIniAsync(path)).ToMetadata());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
