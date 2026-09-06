using System.IO.Compression;
using WheelWizard.Core.Mods;

namespace WheelWizard.Core.Test;

public sealed class ModTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ww mods " + Guid.NewGuid());
    ModLibrary Library => new(Path.Combine(root, "Mods"));

    public ModTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    string Archive(params (string Path, string Content)[] files)
    {
        var path = Path.Combine(root, Guid.NewGuid() + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(file.Path).Open());
            writer.Write(file.Content);
        }
        return path;
    }

    [Fact]
    public async Task ImportMetadataOrderingAndRemovalPersist()
    {
        var archive = Archive(("nested/course.bin", "track"));
        await Library.ImportNext(archive, "First");
        await Library.ImportNext(archive, "Second");
        Assert.Equal(new[] { "First", "Second" }, Library.Load().Select(m => m.Title));
        Assert.Equal("track", File.ReadAllText(Path.Combine(Library.Root, "First/nested/course.bin")));
        Library.SetEnabled("First", false);
        Library.Move("Second", -1);
        Assert.Equal(new[] { "Second", "First" }, Library.Load().Select(m => m.Title));
        Assert.Equal(new[] { 0, 1 }, Library.Load().Select(m => m.Priority));
        Assert.False(Library.Load()[1].IsEnabled);
        Library.Remove("First");
        Assert.Single(Library.Load());
        Assert.True(File.Exists(archive));
        Assert.Empty(Directory.GetDirectories(root, ".ww-mod-*"));
    }

    [Fact]
    public async Task SevenZipImport()
    {
        // A one-file 7z fixture generated with bsdtar, independent of SharpCompress's reader.
        var path = Path.Combine(root, "fixture.7z");
        File.WriteAllBytes(
            path,
            Convert.FromBase64String(
                "N3q8ryccAAOY/9pcEQAAAAAAAABoAAAAAAAAAL39mBgAMxpLeBPqtRo2oF///kXgAAEEBgABCREABwsBAAEjAwEBBV0AAIAADAcACAoB7kDlBQAABQEREwBmAGkAbABlAC4AYgBpAG4AAAAUCgEAFHeZvsc93QESCgEAFHeZvsc93QETCgEAAXWZvsc93QEVBgEAIICkgQAA"
            )
        );
        await Library.ImportNext(path, "SevenZip");
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(Library.Root, "SevenZip/file.bin")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".")]
    public async Task InvalidNamesCannotPublish(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Library.ImportNext(Archive(("a", "x")), name));
        Assert.Empty(Library.Load());
    }

    [Fact]
    public async Task DuplicateOrExistingDestinationIsNeverOverwritten()
    {
        var archive = Archive(("a", "x"));
        await Library.ImportNext(archive, "First");
        await Assert.ThrowsAsync<IOException>(() => Library.ImportNext(archive, "FIRST"));
        Directory.CreateDirectory(Path.Combine(Library.Root, "Incomplete"));
        await Assert.ThrowsAsync<IOException>(() => Library.ImportNext(archive, "Incomplete"));
        Assert.Single(Library.Load());
    }

    [Fact]
    public async Task TraversalAndCancellationLeaveNoPublishedModOrStaging()
    {
        await Assert.ThrowsAsync<IOException>(() => Library.ImportNext(Archive(("valid", "x"), ("../escaped", "bad")), "Bad"));
        Assert.False(File.Exists(Path.Combine(root, "escaped")));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () =>
                Library.ImportNext(
                    Archive(("a", "x"), ("b", "y")),
                    "Cancelled",
                    new CallbackProgress(_ => cancellation.Cancel()),
                    cancellation.Token
                )
        );
        Assert.Empty(Library.Load());
        Assert.Empty(Directory.GetDirectories(root, ".ww-mod-*"));
    }

    [Fact]
    public async Task InvalidArchiveLeavesNoStaging()
    {
        var path = Path.Combine(root, "bad.zip");
        File.WriteAllText(path, "not an archive");
        await Assert.ThrowsAnyAsync<Exception>(() => Library.ImportNext(path, "Bad"));
        Assert.Empty(Library.Load());
        Assert.Empty(Directory.GetDirectories(root, ".ww-mod-*"));
    }

    [Fact]
    public void MetadataRetainsDefaultsAndRoundTrips()
    {
        var path = Path.Combine(root, "mod.ini");
        File.WriteAllText(path, "[Mod]\nName=Example\nModID=invalid\nIsEnabled=invalid\nPriority=invalid\n");
        var mod = ModMetadataFile.Load(path);
        Assert.Equal(-1, mod.ModID);
        Assert.True(mod.IsEnabled);
        Assert.Equal(0, mod.Priority);
        mod = new("Example", "Author", 42, false, 8);
        ModMetadataFile.Save(path, mod);
        Assert.Equal(mod, ModMetadataFile.Load(path));
    }

    [Fact]
    public async Task PlanPreservesPrecedenceFlatteningAndTaggedArchives()
    {
        await Library.ImportNext(Archive(("sub/course.bin", "high"), ("9.Character.tag.szs", "high archive")), "High");
        await Library.ImportNext(Archive(("other/COURSE.bin", "low"), ("Character.tag.szs", "low archive")), "Low");
        var mods = Library.Load();
        var plan = ModLaunchPlanner.Build(Library.Root, mods);
        var conflict = Assert.Single(plan.Files, f => f.Overwritten.Count > 0);
        Assert.Equal("High", conflict.Winner.ModTitle);
        Assert.Equal("Low", Assert.Single(conflict.Overwritten).ModTitle);
        Assert.Contains(plan.Files, f => f.Destination == "1.Character.tag.szs");
        Assert.Contains(plan.Files, f => f.Destination == "2.Character.tag.szs");
        Assert.DoesNotContain(plan.Files, f => f.Destination.EndsWith(".ini"));
        Library.SetEnabled("High", false);
        Assert.All(ModLaunchPlanner.Build(Library.Root, Library.Load()).Files, f => Assert.Equal("Low", f.Winner.ModTitle));
        Assert.Empty(ModLaunchPlanner.Build(Library.Root, [new("Missing")]).Files);
    }

    [Fact]
    public async Task SameModCollisionFollowsActualEnumerationOrder()
    {
        await Library.ImportNext(Archive(("a/file.bin", "a"), ("b/file.bin", "b")), "One");
        var sources = Directory
            .GetFiles(Path.Combine(Library.Root, "One"), "*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith("file.bin"))
            .ToArray();
        var file = Assert.Single(ModLaunchPlanner.Build(Library.Root, Library.Load()).Files);
        Assert.Equal(sources[^1], file.Winner.SourcePath);
        Assert.Equal(sources[0], Assert.Single(file.Overwritten).SourcePath);
    }

    [Fact]
    public async Task CopyUsesPlanCleansOnlyTopLevelAndRetainsSkipRule()
    {
        await Library.ImportNext(Archive(("file.bin", "new")), "One");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        File.WriteAllText(Path.Combine(target, "obsolete.bin"), "old");
        File.WriteAllText(Path.Combine(target, "nested/keep"), "keep");
        var plan = ModLaunchPlanner.Build(Library.Root, Library.Load());
        ModLaunchPlanner.Copy(target, plan);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "file.bin")));
        Assert.False(File.Exists(Path.Combine(target, "obsolete.bin")));
        Assert.True(File.Exists(Path.Combine(target, "nested/keep")));
        File.WriteAllText(Path.Combine(target, "file.bin"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(target, "file.bin"), File.GetLastWriteTimeUtc(plan.Files[0].Winner.SourcePath));
        ModLaunchPlanner.Copy(target, plan);
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "file.bin")));
        var disabled = Library.Load().Select(m => m with { IsEnabled = false }).ToArray();
        Assert.True(ModLaunchPlanner.ShouldAskToClearTargetFolder(target, disabled));
        ModLaunchPlanner.Prepare(Library.Root, target, disabled);
        Assert.True(Directory.Exists(target));
        ModLaunchPlanner.Prepare(Library.Root, target, disabled, true);
        Assert.False(Directory.Exists(target));
    }

    sealed class CallbackProgress(Action<ModProgress> callback) : IProgress<ModProgress>
    {
        public void Report(ModProgress value) => callback(value);
    }
}
