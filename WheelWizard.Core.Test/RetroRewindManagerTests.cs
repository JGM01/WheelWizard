using System.IO.Compression;
using System.Net;
using Semver;
using WheelWizard.Core;

namespace WheelWizard.Core.Test;

public sealed class RetroRewindManagerTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "rr manager test " + Guid.NewGuid());

    public RetroRewindManagerTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    string InstallRoot => Path.Combine(root, "install");

    [Fact]
    public void InstalledVersionReadsValidAndMissing()
    {
        var dir = Path.Combine(InstallRoot, "RetroRewind6");
        Directory.CreateDirectory(dir);
        Assert.Null(RetroRewindManager.InstalledVersion(InstallRoot));
        File.WriteAllText(Path.Combine(dir, "version.txt"), "3.6.0");
        Assert.Equal("3.6.0", RetroRewindManager.InstalledVersion(InstallRoot));
        File.WriteAllText(Path.Combine(dir, "version.txt"), "bad");
        Assert.Null(RetroRewindManager.InstalledVersion(InstallRoot));
    }

    [Theory]
    [InlineData("3.7.0 http://update.rwfc.net:8000/a.zip x desc\n", 1)]
    [InlineData("3.6.0 http://u/x zip a\n3.7.0 https://u/y.zip x nice update\n", 2)]
    [InlineData("3.6.0\ngarbage\n3.7.0 https://u/y.zip x ok\n", 1)]
    public void ParseVersionFileSkipsMalformed(string text, int expected)
    {
        var entries = RetroRewindManager.ParseVersionFile(text);
        Assert.Equal(expected, entries.Count);
        Assert.All(entries, e => Assert.True(SemVersion.TryParse(e.Version.ToString(), out _)));
    }

    [Fact]
    public void ParseDeletionFileSkipsMalformed()
    {
        var deletions = RetroRewindManager.ParseDeletionFile("3.6.1 old/file\nbad line\ngarbage\n3.7.0 other/path\n");
        Assert.Equal(2, deletions.Count);
        Assert.Contains(deletions, d => d.Path == "other/path");
    }

    [Fact]
    public void UpdatesAndDeletionsSelectRanges()
    {
        var current = SemVersion.Parse("3.6.0");
        var updates = RetroRewindManager.ParseVersionFile("3.6.0 u x old\n3.6.1 u x mid\n3.7.0 u x new\n");
        var after = RetroRewindManager.UpdatesAfter(current, updates);
        Assert.Equal(["3.6.1", "3.7.0"], after.Select(u => u.Version.ToString()));

        var deletions = RetroRewindManager.ParseDeletionFile("3.5.0 old\n3.6.1 mid\n3.7.0 last\n3.7.1 too\n");
        var between = RetroRewindManager.DeletionsBetween(current, SemVersion.Parse("3.7.0"), deletions);
        Assert.Equal(["3.6.1", "3.7.0"], between.Select(d => d.Version.ToString()));
    }

    [Theory]
    [InlineData("../escape", null)]
    [InlineData("../../escape", null)]
    [InlineData("/RetroRewind6/x", "RetroRewind6/x")]
    [InlineData("a/../RetroRewind6/x", "RetroRewind6/x")]
    [InlineData("RetroRewind6\\x", "RetroRewind6/x")]
    public void ResolveWithinConfinesPaths(string input, string? expectedRelative)
    {
        var resolved = RetroRewindManager.ResolveWithin(InstallRoot, input);
        if (expectedRelative == null)
        {
            Assert.Null(resolved);
            return;
        }
        Assert.NotNull(resolved);
        Assert.Equal(expectedRelative.Replace('/', Path.DirectorySeparatorChar), Path.GetRelativePath(InstallRoot, resolved!));
    }

    [Fact]
    public void UninstallRemovesOnlyRrOwnedEntries()
    {
        Directory.CreateDirectory(Path.Combine(InstallRoot, "RetroRewind6"));
        Directory.CreateDirectory(Path.Combine(InstallRoot, "riivolution"));
        File.WriteAllText(Path.Combine(InstallRoot, "riivolution", "RetroRewind6.xml"), "xml");
        File.WriteAllText(Path.Combine(InstallRoot, "riivolution", "OtherMod.xml"), "keep");
        File.WriteAllText(Path.Combine(InstallRoot, "unrelated.txt"), "keep");

        RetroRewindManager.UninstallAsync(InstallRoot);

        Assert.False(Directory.Exists(Path.Combine(InstallRoot, "RetroRewind6")));
        Assert.False(File.Exists(Path.Combine(InstallRoot, "riivolution", "RetroRewind6.xml")));
        Assert.True(File.Exists(Path.Combine(InstallRoot, "riivolution", "OtherMod.xml")));
        Assert.True(File.Exists(Path.Combine(InstallRoot, "unrelated.txt")));
    }

    [Fact]
    public async Task InstallMovesRrOwnedEntriesOnly()
    {
        var zip = Zip(
            ("RetroRewind6/version.txt", "3.6.0"),
            ("RetroRewind6/Binaries/Code.pul", "code"),
            ("riivolution/RetroRewind6.xml", "xml"),
            ("unrelated/apps/boot.dol", "app")
        );
        using var http = new HttpClient(
            new Handler(
                (req, ct) =>
                    Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = req.RequestUri!.AbsolutePath.EndsWith(".txt")
                                ? new StringContent("https://example.test/package.zip")
                                : new ByteArrayContent(zip),
                        }
                    )
            )
        );
        await new RetroRewindManager(http).InstallAsync(InstallRoot, null, default);

        Assert.Equal("3.6.0", RetroRewindManager.InstalledVersion(InstallRoot));
        Assert.True(File.Exists(Path.Combine(InstallRoot, "RetroRewind6", "Binaries", "Code.pul")));
        Assert.True(File.Exists(Path.Combine(InstallRoot, "riivolution", "RetroRewind6.xml")));
        Assert.False(File.Exists(Path.Combine(InstallRoot, "unrelated", "apps", "boot.dol")));
    }

    [Fact]
    public async Task UpdateAppliesDeletionsThenNewestVersion()
    {
        Directory.CreateDirectory(Path.Combine(InstallRoot, "RetroRewind6", "Binaries"));
        File.WriteAllText(Path.Combine(InstallRoot, "RetroRewind6", "version.txt"), "3.6.0");
        File.WriteAllText(Path.Combine(InstallRoot, "RetroRewind6", "Binaries", "Code.pul"), "old");
        File.WriteAllText(Path.Combine(InstallRoot, "RetroRewind6", "obsolete.txt"), "remove me");

        const string versionFile = "3.6.0 https://example.test/old.zip x old\n3.7.0 https://example.test/new.zip x new\n";
        const string deleteFile = "3.6.1 RetroRewind6/obsolete.txt\n";
        var updateZip = Zip(("RetroRewind6/Binaries/Code.pul", "new code"));
        using var http = new HttpClient(
            new Handler(
                (req, ct) =>
                {
                    var path = req.RequestUri!.AbsolutePath;
                    return Task.FromResult(
                        path.EndsWith("RetroRewindVersion.txt")
                            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(versionFile) }
                            : path.EndsWith("RetroRewindDelete.txt")
                                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(deleteFile) }
                                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(updateZip) }
                    );
                }
            )
        );

        await new RetroRewindManager(http).UpdateAsync(InstallRoot, null, default);

        Assert.Equal("3.7.0", RetroRewindManager.InstalledVersion(InstallRoot));
        Assert.Equal("new code", File.ReadAllText(Path.Combine(InstallRoot, "RetroRewind6", "Binaries", "Code.pul")));
        Assert.False(File.Exists(Path.Combine(InstallRoot, "RetroRewind6", "obsolete.txt")));
    }

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => response(r, ct);
    }

    static byte[] Zip(params (string Name, string Value)[] files)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var (name, value) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(value);
            }
        return bytes.ToArray();
    }
}
