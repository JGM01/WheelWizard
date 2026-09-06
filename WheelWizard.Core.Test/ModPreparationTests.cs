using System.Text.Json;
using WheelWizard.Core.Mods;

namespace WheelWizard.Core.Test;

public sealed class ModPreparationTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ww-patch-tests-" + Guid.NewGuid());
    string Library => Path.Combine(root, "Mods");
    ModPreparation Preparation => new(Path.Combine(root, "RR", "Patches"), Path.Combine(root, "Transactions"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    string Put(string relative, string contents = "content")
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    [Theory]
    [InlineData("revo_kart.brsar", true)]
    [InlineData("[123].brwsd", true)]
    [InlineData("[123].brbnk", true)]
    [InlineData("123.brwsd", false)]
    [InlineData("MenuSingle.szs", true)]
    [InlineData("1.Example.MenuSingle.szs", false)]
    [InlineData("bds-allkart.szs", false)]
    [InlineData("file.bin", false)]
    public void SharedConversionRules(string name, bool needsConversion) =>
        Assert.Equal(needsConversion, ModCompatibility.ConversionReason(name) != null);

    [Fact]
    public void StagedCopyRetainsPrecedenceAndExistingSubdirectories()
    {
        Put("Mods/High/file.bin", "winner");
        Put("Mods/Low/file.bin", "loser");
        Put("Mods/High/Example.MenuSingle.szs");
        Put("RR/Patches/stale.bin");
        Put("RR/Patches/nested/manual.bin", "retained");
        Preparation.Prepare(Library, [new("High"), new("Low", Priority: 1)], false, true);
        Assert.Equal("winner", File.ReadAllText(Path.Combine(Preparation.Target, "file.bin")));
        Assert.True(File.Exists(Path.Combine(Preparation.Target, "0.Example.MenuSingle.szs")));
        Assert.False(File.Exists(Path.Combine(Preparation.Target, "stale.bin")));
        Assert.True(File.Exists(Path.Combine(Preparation.Target, "nested/manual.bin")));
        Assert.Null(Preparation.Recovery);
        Assert.False(File.Exists(Preparation.RecordPath));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "RR"), ".ww-patches-*"));
    }

    [Fact]
    public void CompatibilityAndMissingModsBlockWithoutChangingTarget()
    {
        Put("RR/Patches/original.bin", "original");
        Put("Mods/Incompatible/MenuSingle.szs");
        var error = Assert.Throws<ModCompatibilityException>(() => Preparation.Prepare(Library, [new("Incompatible")], false, true));
        Assert.Equal("MenuSingle.szs", Assert.Single(error.Findings).RelativePath);
        Assert.Throws<DirectoryNotFoundException>(() => Preparation.Prepare(Library, [new("Missing")], false, true));
        Preparation.Prepare(Library, [new("Missing", IsEnabled: false)], false, true);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Preparation.Target, "original.bin")));
    }

    [Fact]
    public void KeepCannotBypassCompatibilityButDeleteCanRemoveBlockedFiles()
    {
        Put("RR/Patches/MenuSingle.szs");
        Assert.Throws<ModCompatibilityException>(() => Preparation.Prepare(Library, [], false, true));
        Preparation.Prepare(Library, [], true, true);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Preparation.Target));
    }

    [Fact]
    public void FrameworkCanPrepareConversionRequiredFiles()
    {
        Put("Mods/Legacy/MenuSingle.szs");
        Preparation.Prepare(Library, [new("Legacy")], false);
        Assert.True(File.Exists(Path.Combine(Preparation.Target, "MenuSingle.szs")));
    }

    [Fact]
    public void CancellationDuringCopyPreservesPreviousSet()
    {
        Put("RR/Patches/original.bin", "original");
        Put("Mods/New/a.bin"); Put("Mods/New/b.bin");
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => Preparation.Prepare(Library, [new("New")], false,
            progress: new CallbackProgress(_ => cancellation.Cancel()), ct: cancellation.Token));
        Assert.Equal(new[] { "original.bin" }, Directory.GetFiles(Preparation.Target).Select(Path.GetFileName));
        Assert.False(File.Exists(Preparation.RecordPath));
    }

    [Fact]
    public void CancellationAtCommitBoundaryDoesNotLeavePartialPatches()
    {
        Put("RR/Patches/original.bin"); Put("Mods/New/new.bin");
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => Preparation.Prepare(Library, [new("New")], false,
            phase: phase => { if (phase == "publishing") cancellation.Cancel(); }, ct: cancellation.Token));
        Assert.Equal(new[] { "new.bin" }, Directory.GetFiles(Preparation.Target).Select(Path.GetFileName));
        Assert.Null(Preparation.Recovery);
    }

    [Theory]
    [InlineData("before-backup")]
    [InlineData("after-backup")]
    [InlineData("after-publish")]
    public void InterruptedPublicationRequiresExplicitRetryableRestoration(string position)
    {
        var target = Preparation.Target;
        var original = Put("RR/Patches/original.bin", "original");
        var stage = Path.Combine(root, "RR", ".ww-patches-fixture");
        var backup = stage + "-previous";
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "new.bin"), "new");
        if (position != "before-backup") Directory.Move(target, backup);
        if (position == "after-publish") Directory.Move(stage, target);
        Journal(stage, backup, true);
        Assert.NotNull(Preparation.Recovery);
        Assert.Throws<InvalidOperationException>(() => Preparation.Prepare(Library, [], true));
        Preparation.Restore();
        Preparation.Restore();
        Assert.Equal("original", File.ReadAllText(original));
        Assert.False(File.Exists(Path.Combine(target, "new.bin")));
        Assert.Null(Preparation.Recovery);
    }

    [Fact]
    public void RestoreMissingPreviousTargetAndCommittedCleanup()
    {
        var stage = Path.Combine(root, "RR", ".ww-patches-fixture");
        Put("RR/Patches/new.bin");
        Journal(stage, stage + "-previous", false);
        Preparation.Restore();
        Assert.False(Directory.Exists(Preparation.Target));
        Put("RR/Patches/committed.bin");
        Put("RR/.ww-patches-fixture-previous/old.bin");
        Journal(stage, stage + "-previous", true, "committed");
        Assert.Null(Preparation.Recovery);
        Preparation.Restore(); // only garbage collection for a completed transaction
        Assert.True(File.Exists(Path.Combine(Preparation.Target, "committed.bin")));
    }

    [Fact]
    public void AmbiguousRecoveryPreservesEvidenceAndBlocksLaunchPreparation()
    {
        Put("RR/Patches/uncertain.bin");
        var stage = Path.Combine(root, "RR", ".ww-patches-fixture");
        Journal(stage, stage + "-previous", true);
        Assert.Throws<IOException>(Preparation.Restore);
        Assert.NotNull(Preparation.Recovery);
        Assert.True(File.Exists(Path.Combine(Preparation.Target, "uncertain.bin")));
        File.WriteAllText(Preparation.RecordPath, "corrupt");
        Assert.NotNull(Preparation.Recovery);
        Assert.Throws<InvalidOperationException>(Preparation.EnsureReady);
    }

    [Fact]
    public void PublicationFailureRollsBackThePreviousDirectory()
    {
        Put("RR/Patches/original.bin", "original"); Put("Mods/New/new.bin");
        Assert.Throws<DirectoryNotFoundException>(() => Preparation.Prepare(Library, [new("New")], false,
            phase: phase =>
            {
                // Remove the staged directory immediately before publication: the first rename
                // succeeds, the second fails, exercising the real filesystem rollback path.
                if (phase == "publishing")
                    Directory.Delete(Directory.GetDirectories(Path.Combine(root, "RR"), ".ww-patches-*").Single(), true);
            }));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Preparation.Target, "original.bin")));
        Assert.Null(Preparation.Recovery);
        Assert.False(File.Exists(Preparation.RecordPath));
    }

    [Fact]
    public void FailedRestorationKeepsBackupUntilExplicitRetrySucceeds()
    {
        if (OperatingSystem.IsWindows()) return;
        var stage = Path.Combine(root, "RR", ".ww-patches-fixture");
        var backup = stage + "-previous";
        Put("RR/.ww-patches-fixture-previous/original.bin", "original");
        Put("RR/Patches/new.bin");
        File.CreateSymbolicLink(Path.Combine(backup, "linked.bin"), Path.Combine(backup, "original.bin"));
        Journal(stage, backup, true);
        Assert.Throws<IOException>(Preparation.Restore);
        Assert.NotNull(Preparation.Recovery);
        Assert.True(File.Exists(Path.Combine(backup, "original.bin")));
        Assert.True(File.Exists(Path.Combine(Preparation.Target, "new.bin")));
        File.Delete(Path.Combine(backup, "linked.bin"));
        Preparation.Restore();
        Assert.Equal("original", File.ReadAllText(Path.Combine(Preparation.Target, "original.bin")));
        Assert.Null(Preparation.Recovery);
    }

    void Journal(string stage, string backup, bool hadTarget, string phase = "publishing")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Preparation.RecordPath)!);
        File.WriteAllText(Preparation.RecordPath, JsonSerializer.Serialize(new { Stage = stage, Backup = backup, HadTarget = hadTarget, Phase = phase }));
    }
    sealed class CallbackProgress(Action<ModProgress> action) : IProgress<ModProgress>
    { public void Report(ModProgress value) => action(value); }
}
