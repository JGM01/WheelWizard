using WheelWizard.Core;
using WheelWizard.Host;

namespace WheelWizard.Core.Test;

public sealed class WorkflowTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "native workflow " + Guid.NewGuid());

    public WorkflowTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, true);

    async Task<(Workflow Flow, SetupInput Setup)> Fixture()
    {
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "Launcher"));
        Directory.CreateDirectory(Path.Combine(workspace, "projects/mkwii"));
        File.WriteAllText(Path.Combine(workspace, "projects/mkwii/recomp.yml"), "fixture");
        var game = Path.Combine(root, "game with spaces.wbfs");
        File.WriteAllText(game, "fixture disc");
        foreach (
            var args in new[]
            {
                new[] { "init" },
                new[] { "add", "." },
                new[] { "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "commit", "-m", "fixture" },
            }
        )
            Assert.Equal(0, await ChildProcess.Run("/usr/bin/git", args, workspace, (_, _) => { }, default));
        var flow = new Workflow(Path.Combine(root, "managed"), root);
        Directory.CreateDirectory(Path.Combine(flow.Package, "RetroRewind6/Binaries"));
        Directory.CreateDirectory(Path.Combine(flow.Package, "riivolution"));
        File.WriteAllText(Path.Combine(flow.Package, "RetroRewind6/Binaries/Code.pul"), "fixture code");
        File.WriteAllText(Path.Combine(flow.Package, "RetroRewind6/version.txt"), "3.6.0");
        File.WriteAllText(Path.Combine(flow.Package, "riivolution/RetroRewind6.xml"), "fixture");
        return (flow, new(game, workspace, "/bin/bash", "/bin/bash", "/bin/bash", "/bin/bash"));
    }

    static void Script(SetupInput setup, string body) =>
        File.WriteAllText(Path.Combine(setup.Workspace, "Launcher/local-build-macos.command"), body.Replace("\r\n", "\n"));

    const string SuccessfulBuild = """
        set -eu
        while (($#)); do
          case "$1" in
            --output-dir) out="$2"; shift 2;;
            *) shift;;
          esac
        done
        echo MKWCBUILD:STEP:compile fixture
        for app in WiiCompiled RetroRewind; do
          mkdir -p "$out/$app.app/Contents/MacOS"
          printf '#!/bin/bash\necho %s\ncat UserData/Config.toml\n' "$app" > "$out/$app.app/Contents/MacOS/$app"
          chmod +x "$out/$app.app/Contents/MacOS/$app"
        done
        """;

    [Fact]
    public async Task BuildLaunchIdentitiesAndInputChanges()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var (flow, setup) = await Fixture();
        Script(setup, SuccessfulBuild);
        var lines = new List<string>();
        await flow.Build(setup, "retro-rewind", (k, v) => lines.Add(k), default);
        Assert.Contains("progress", lines);
        Assert.All(await flow.Status(setup, default), s => Assert.True(s.Ready));
        var config = new RuntimeConfiguration(new Testably.Abstractions.RealFileSystem());
        foreach (var product in new[] { "base", "retro-rewind" })
        {
            lines.Clear();
            Assert.Equal(
                0,
                await flow.Launch(
                    setup,
                    product,
                    (k, v) =>
                    {
                        lock (lines)
                            lines.Add(System.Text.Json.JsonSerializer.Serialize(v));
                    },
                    default
                )
            );
            Assert.Contains(lines, l => l.Contains(product == "base" ? "WiiCompiled" : "RetroRewind"));
            Assert.Equal(
                product == "base" ? "\"\"" : RuntimeConfiguration.Format(Path.Combine(flow.Package, "RetroRewind6")),
                config.Read(flow.Config, "paths", "retro_rewind_root")
            );
            Assert.Equal("[]", config.Read(flow.Config, "paths", "overlay_roots"));
        }
        File.AppendAllText(setup.Wbfs, "changed");
        Assert.All(await flow.Status(setup, default), s => Assert.False(s.Ready));
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.Launch(setup, "base", (_, _) => { }, default));
    }

    [Fact]
    public async Task FailedAndCancelledBuildPreservesPublishedState()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var (flow, setup) = await Fixture();
        Script(setup, SuccessfulBuild);
        await flow.Build(setup, "base", (_, _) => { }, default);
        string before = File.ReadAllText(Path.Combine(flow.Runtime, "base.json"));
        string config = File.ReadAllText(flow.Config);
        Script(setup, "echo failed >&2; exit 9");
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.Build(setup, "base", (_, _) => { }, default));
        Assert.Equal(before, File.ReadAllText(Path.Combine(flow.Runtime, "base.json")));
        Assert.Equal(config, File.ReadAllText(flow.Config));
        using var cts = new CancellationTokenSource();
        Script(setup, "echo waiting; sleep 120");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.Build(setup, "base", (_, _) => cts.Cancel(), cts.Token));
        Assert.Equal(before, File.ReadAllText(Path.Combine(flow.Runtime, "base.json")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(flow.Root, "Staging")));
        Assert.True((await flow.Status(setup, default))[0].Ready);
    }

    [Fact]
    public async Task SuccessfulExitWithoutAppNeverPublishes()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var (flow, setup) = await Fixture();
        Script(setup, "exit 0");
        await Assert.ThrowsAsync<InvalidDataException>(() => flow.Build(setup, "base", (_, _) => { }, default));
        Assert.All(await flow.Status(setup, default), s => Assert.False(s.Ready));
    }
}
