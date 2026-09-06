using System.IO.Compression;
using System.Net;
using WheelWizard.Core;
using WheelWizard.Core.Recomp;

namespace WheelWizard.Core.Test;

public sealed class ServicesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "wheel wizard test " + Guid.NewGuid());

    public ServicesTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    RuntimeConfiguration Config() => new(new Testably.Abstractions.RealFileSystem());

    [Theory]
    [InlineData("3.2.6", "3.2.6")]
    [InlineData(" 3.5.0\n", "3.5.0")]
    [InlineData("v3.2.6", null)]
    [InlineData("bad", null)]
    public void InstalledVersion(string input, string? expected) => Assert.Equal(expected, RetroRewindPackage.InstalledVersion(input));

    [Fact]
    public void LatestAndUrl()
    {
        Assert.Equal("3.6.0", RetroRewindPackage.LatestVersion("3.2.6 a b old\n3.6.0 a b new\n"));
        Assert.Equal(
            "https://update.rwfc.net/file.zip",
            RetroRewindPackage.ResolveUrl(" http://update.rwfc.net:8000/file.zip\n").AbsoluteUri
        );
        Assert.Throws<InvalidDataException>(() => RetroRewindPackage.ResolveUrl("file:///tmp/bad"));
    }

    [Fact]
    public void SettingsPreserveUnknownAndEscapePaths()
    {
        var path = Path.Combine(root, "Config.toml");
        File.WriteAllText(path, "# keep\n[other]\nopaque = [1, 2]\n[audio]\nvolume = 0.5 # old\n");
        var c = Config();
        c.WriteSettings(path, new(0.7, 1.5));
        var text = "C:\\games\\a\"b\nnext";
        c.Write(path, [new("paths", "dvd_root", RuntimeConfiguration.Format(text)), new("network", "enabled", "false")]);
        Assert.Equal(text, RuntimeConfiguration.Parse(c.Read(path, "paths", "dvd_root")!, typeof(string)));
        Assert.Equal(new RuntimeSettings(0.7, 1.5), c.ReadSettings(path));
        Assert.Contains("opaque = [1, 2]", File.ReadAllText(path));
        Assert.Contains("# keep", File.ReadAllText(path));
        Assert.Equal(false, RuntimeConfiguration.Parse(c.Read(path, "network", "enabled")!, typeof(bool)));
    }

    [Theory]
    [InlineData("oops")]
    [InlineData("nan")]
    [InlineData("\"0.5\"")]
    public void MalformedValuesAreNotOverwritten(string value)
    {
        var path = Path.Combine(root, "Config.toml");
        var original = $"[audio]\nvolume = {value}\n";
        File.WriteAllText(path, original);
        Assert.Throws<FormatException>(() => Config().ReadSettings(path));
        Assert.Throws<FormatException>(() => Config().WriteSettings(path, new()));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void DuplicateValuesAreReported()
    {
        var path = Path.Combine(root, "Config.toml");
        File.WriteAllText(path, "[audio]\nvolume=1\nvolume=0\n");
        Assert.Throws<FormatException>(() => Config().ReadSettings(path));
    }

    [Fact]
    public void MalformedSupportedArrayIsNotOverwritten()
    {
        var path = Path.Combine(root, "Config.toml");
        const string original = "[paths]\noverlay_roots = invalid\n";
        File.WriteAllText(path, original);
        Assert.Throws<FormatException>(() => Config().Write(path, [new("paths", "overlay_roots", "[]")]));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void BooleanValuesRoundTrip(string literal, bool value)
    {
        Assert.Equal(value, RuntimeConfiguration.Parse(literal, typeof(bool)));
        Assert.Equal(literal, RuntimeConfiguration.Format(value));
        Assert.Throws<FormatException>(() => RuntimeConfiguration.Parse("yes", typeof(bool)));
    }

    byte[] Zip(params (string Name, string Value)[] files)
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

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("a/../../escape")]
    [InlineData("..\\escape")]
    public void RejectsUnsafeArchive(string name)
    {
        var zip = Path.Combine(root, "test.zip");
        File.WriteAllBytes(zip, Zip((name, "bad")));
        Assert.Throws<InvalidDataException>(() => RetroRewindPackage.Extract(zip, Path.Combine(root, "out"), null, default));
    }

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => response(r, ct);
    }

    [Fact]
    public async Task SuccessfulStageValidatesPackage()
    {
        var zip = Zip(
            ("RetroRewind6/version.txt", "3.6.0"),
            ("RetroRewind6/Binaries/Code.pul", "code"),
            ("riivolution/RetroRewind6.xml", "xml")
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
        var stage = await new RetroRewindPackage(http).StageAsync(root, null, default);
        Assert.Equal("3.6.0", stage.Version);
        Assert.True(File.Exists(Path.Combine(stage.Path, "content/RetroRewind6/Binaries/Code.pul")));
        Assert.False(File.Exists(Path.Combine(stage.Path, "package.zip")));
    }

    [Fact]
    public async Task UnavailableEndpoint()
    {
        using var http = new HttpClient(
            new Handler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
        );
        await Assert.ThrowsAsync<HttpRequestException>(() => new RetroRewindPackage(http).StageAsync(root, null, default));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task InterruptedDownloadCleansOnlyOwnedStage()
    {
        File.WriteAllText(Path.Combine(root, "keep"), "keep");
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(
            new Handler(
                async (req, ct) =>
                {
                    if (req.RequestUri!.AbsolutePath.EndsWith(".txt"))
                        return new(HttpStatusCode.OK) { Content = new StringContent("https://example.test/package.zip") };
                    cts.Cancel();
                    await Task.Delay(1, ct);
                    return new(HttpStatusCode.OK);
                }
            )
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RetroRewindPackage(http).StageAsync(root, null, cts.Token));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task MissingPackageFilesCleansStage()
    {
        using var http = new HttpClient(
            new Handler(
                (req, ct) =>
                    Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = req.RequestUri!.AbsolutePath.EndsWith(".txt")
                                ? new StringContent("https://example.test/package.zip")
                                : new ByteArrayContent(Zip(("wrong/file", "bad"))),
                        }
                    )
            )
        );
        await Assert.ThrowsAsync<InvalidDataException>(() => new RetroRewindPackage(http).StageAsync(root, null, default));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task ChildDrainsBothStreamsAndPreservesArguments()
    {
        var lines = new System.Collections.Concurrent.ConcurrentBag<string>();
        int code = await ChildProcess.Run(
            "/bin/bash",
            ["-c", "printf '%s\\n' \"$1\"; for ((i=0;i<4000;i++)); do echo err >&2; done; exit 7", "--", "path with spaces $literal"],
            root,
            (stream, line) => lines.Add(line),
            default
        );
        Assert.Equal(7, code);
        Assert.Contains("path with spaces $literal", lines);
        Assert.Equal(4001, lines.Count);
    }

    [Fact]
    public async Task CancellationStopsChild()
    {
        using var cts = new CancellationTokenSource();
        var task = ChildProcess.Run("/bin/bash", ["-c", "echo started; sleep 120"], root, (s, l) => cts.Cancel(), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void PublicationFailureRestoresAppsAndReceipts()
    {
        var stage = Path.Combine(root, "stage");
        var target = Path.Combine(root, "Runtime");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "app"), "working");
        File.WriteAllText(Path.Combine(target, "receipt"), "old");
        File.WriteAllText(Path.Combine(stage, "app"), "new");
        Assert.Throws<FileNotFoundException>(() => ProductWorkflow.Publish(stage, target, ["app", "receipt"]));
        Assert.Equal("working", File.ReadAllText(Path.Combine(target, "app")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "receipt")));
    }

    [Theory]
    [InlineData("base", "base", false)]
    [InlineData("retro-rewind", "both", true)]
    public void BuildModes(string id, string profile, bool offline)
    {
        var args = ProductWorkflow.BuildArguments(
            new("/game with spaces.wbfs", "/workspace with spaces", "/cmake", "/ninja", "/nodtool", "/translator"),
            id,
            root,
            "/managed RR"
        );
        Assert.Equal(profile, args[args.IndexOf("--profile") + 1]);
        Assert.Equal(offline, args.Contains("--skip-retro-wfc-payload"));
        Assert.Contains("/game with spaces.wbfs", args);
    }
}
