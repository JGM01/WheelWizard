using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;

namespace WheelWizard.Core.Recomp;

// Owns the native recomp product lifecycle: resolving build inputs, preflight checks,
// status against persisted receipts, Retro Rewind package install, building and
// publishing the product apps, runtime configuration and launching. It is a pure
// frontend-agnostic service; WheelWizard.Host is only the protocol transport in front
// of it.
public sealed class ProductWorkflow(string root, string toolsDirectory)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Root => root;
    public string Runtime => Path.Combine(root, "Runtime");
    public string Config => Path.Combine(Runtime, "UserData", "Config.toml");
    public string Package => Path.Combine(root, "RetroRewind");
    public string Logs => Path.Combine(root, "Logs");
    readonly RuntimeConfiguration config = new(new Testably.Abstractions.RealFileSystem());

    static Product ProductFor(string id) => RecompProducts.ById(id);

    string Executable(Product product) =>
        Path.Combine(Runtime, product.App + ".app", "Contents", "MacOS", product.App);

    string Receipt(Product product) => Path.Combine(Runtime, product.Id + ".json");

    public SetupInput Discover(SetupInput s) =>
        s with
        {
            Cmake = Tool(s.Cmake, "cmake"),
            Ninja = Tool(s.Ninja, "ninja"),
            Nodtool = Tool(s.Nodtool, "nodtool"),
            Translator = Tool(s.Translator, "Translator.Cli"),
        };

    string Tool(string value, string name)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        return new[] { toolsDirectory, Path.Combine(toolsDirectory, "translator"), "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" }
                .Select(p => Path.Combine(p, name))
                .FirstOrDefault(File.Exists) ?? "";
    }

    public string[] Preflight(SetupInput s)
    {
        var errors = new List<string>();
        if (
            !OperatingSystem.IsMacOS()
            || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.Arm64
        )
            errors.Add("Apple Silicon macOS is required");
        foreach (
            var (name, path) in new[]
            {
                ("WBFS", s.Wbfs),
                ("build script", Path.Combine(s.Workspace, "Launcher/local-build-macos.command")),
                ("translation project", Path.Combine(s.Workspace, "projects/mkwii/recomp.yml")),
                ("cmake", s.Cmake),
                ("ninja", s.Ninja),
                ("nodtool", s.Nodtool),
                ("translator", s.Translator),
                ("clang", "/usr/bin/clang"),
                ("codesign", "/usr/bin/codesign"),
            }
        )
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                errors.Add($"Missing {name}: {path}");
        foreach (var path in new[] { s.Cmake, s.Ninja, s.Nodtool, s.Translator })
            if (
                File.Exists(path)
                && !OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0
            )
                errors.Add($"Not executable: {path}");
        return errors.ToArray();
    }

    static async Task<string> Hash(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    async Task<ProductInput> Identity(SetupInput s, string id, CancellationToken ct)
    {
        string revision = "";
        var exit = await ChildProcess.Run(
            "/usr/bin/git",
            ["-C", s.Workspace, "rev-parse", "HEAD"],
            root,
            (stream, line) =>
            {
                if (stream == "stdout")
                    revision += line;
            },
            ct
        );
        if (exit != 0 || revision.Length == 0)
            throw new InvalidOperationException("Cannot determine workspace revision");
        return new(
            Path.GetFullPath(s.Wbfs),
            await Hash(s.Wbfs, ct),
            Path.GetFullPath(s.Workspace),
            revision,
            id == "retro-rewind" ? await Hash(Path.Combine(Package, "RetroRewind6/Binaries/Code.pul"), ct) : ""
        );
    }

    public async Task<ProductStatus[]> Status(SetupInput s, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var statuses = new List<ProductStatus>();
        foreach (var product in RecompProducts.All)
        {
            bool ready = false;
            string detail = "Build required";
            try
            {
                if (File.Exists(Receipt(product)) && File.Exists(Executable(product)))
                {
                    var receipt = JsonSerializer.Deserialize<ProductReceipt>(await File.ReadAllTextAsync(Receipt(product), ct), Json);
                    ready = receipt?.ProductId == product.Id && receipt.Input == await Identity(s, product.Id, ct);
                    detail = ready ? "Ready" : "Inputs changed — rebuild required";
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                detail = e.Message;
            }
            statuses.Add(new(product.Id, product.Name, ready, detail));
        }
        return statuses.ToArray();
    }

    public object PackageStatus()
    {
        try
        {
            var version = RetroRewindManager.InstalledVersion(Package);
            return new
            {
                installed = version != null,
                version,
                error = (string?)null,
            };
        }
        catch (Exception e)
        {
            return new
            {
                installed = Directory.Exists(Package),
                version = (string?)null,
                error = e.Message,
            };
        }
    }

    void RequirePreflight(SetupInput s)
    {
        var errors = Preflight(s);
        if (errors.Length > 0)
            throw new InvalidOperationException(string.Join("\n", errors));
    }

    public async Task Install(SetupInput s, Action<string, object> emit, CancellationToken ct)
    {
        RequirePreflight(s);
        if (Directory.Exists(Package))
            throw new InvalidOperationException("An RR installation already exists. This MVP only supports fresh installation.");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await new RetroRewindManager(http).InstallAsync(
            Package,
            new InlineProgress(p => emit("progress", p)),
            ct
        );
    }

    sealed class InlineProgress(Action<PackageProgress> action) : IProgress<PackageProgress>
    {
        public void Report(PackageProgress value) => action(value);
    }

    public static List<string> BuildArguments(SetupInput s, string id, string stage, string package)
    {
        _ = ProductFor(id);
        List<string> args =
        [
            Path.Combine(s.Workspace, "Launcher/local-build-macos.command"),
            "--workspace",
            s.Workspace,
            "--game",
            s.Wbfs,
            "--nodtool",
            s.Nodtool,
            "--translator-bin",
            s.Translator,
            "--cmake",
            s.Cmake,
            "--ninja",
            s.Ninja,
            "--profile",
            id == "base" ? "base" : "both",
            "--output-dir",
            stage,
        ];
        if (id == "retro-rewind")
            args.AddRange(
                [
                    "--base-output-dir",
                    stage,
                    "--retro-rewind-package-dir",
                    Path.Combine(package, "RetroRewind6"),
                    "--skip-retro-wfc-payload",
                ]
            );
        return args;
    }

    public async Task Build(SetupInput s, string id, Action<string, object> emit, CancellationToken ct)
    {
        RequirePreflight(s);
        _ = ProductFor(id);
        if (id == "retro-rewind")
            RetroRewindPackage.Validate(Package);
        var selected = id == "base" ? new[] { "base" } : new[] { "base", "retro-rewind" };
        var identities = new Dictionary<string, ProductInput>();
        foreach (var product in selected)
            identities[product] = await Identity(s, product, ct);
        var stage = Path.Combine(root, "Staging", "build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var scratch = Path.Combine(stage, "tmp");
            Directory.CreateDirectory(scratch);
            int exit = await ChildProcess.Run(
                "/bin/bash",
                BuildArguments(s, id, stage, Package),
                s.Workspace,
                (stream, line) =>
                {
                    emit(line.StartsWith("MKWCBUILD:STEP:") ? "progress" : "log", new { stage = line, stream });
                },
                ct,
                new Dictionary<string, string> { ["TMPDIR"] = scratch + Path.DirectorySeparatorChar }
            );
            if (exit != 0)
                throw new InvalidOperationException($"Build exited with code {exit}");
            foreach (var productId in selected)
            {
                var product = ProductFor(productId);
                if (!File.Exists(Path.Combine(stage, product.App + ".app", "Contents/MacOS", product.App)))
                    throw new InvalidDataException("Build did not produce " + productId);
                if (identities[productId] != await Identity(s, productId, ct))
                    throw new InvalidOperationException("Inputs changed during compilation");
                await File.WriteAllTextAsync(
                    Path.Combine(stage, productId + ".json"),
                    JsonSerializer.Serialize(new ProductReceipt(productId, identities[productId], DateTimeOffset.UtcNow), Json),
                    ct
                );
            }
            ct.ThrowIfCancellationRequested();
            // Publication is deliberately non-cancellable; rollback includes receipts and config.
            Directory.CreateDirectory(Runtime);
            var stagedConfig = Path.Combine(stage, "UserData/Config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(stagedConfig)!);
            if (File.Exists(Config))
                File.Copy(Config, stagedConfig);
            Configure(s, id, stagedConfig);
            File.WriteAllText(Path.Combine(stage, "portable.txt"), "");
            Publish(
                stage,
                Runtime,
                selected.SelectMany(p => new[] { ProductFor(p).App + ".app", p + ".json" }).Concat(["UserData/Config.toml", "portable.txt"]).ToArray()
            );
        }
        finally
        {
            if (Directory.Exists(stage) && !File.Exists(Path.Combine(stage, "recovery.required")))
                Directory.Delete(stage, true);
        }
    }

    public static void Publish(string stage, string destination, string[] names)
    {
        var backup = Path.Combine(stage, "previous");
        Directory.CreateDirectory(backup);
        var moved = new List<string>();
        var saved = new List<string>();
        static void Move(string a, string b)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(b)!);
            if (Directory.Exists(a))
                Directory.Move(a, b);
            else
                File.Move(a, b);
        }
        try
        {
            foreach (var name in names)
            {
                var target = Path.Combine(destination, name);
                if (File.Exists(target) || Directory.Exists(target))
                {
                    Move(target, Path.Combine(backup, name));
                    saved.Add(name);
                }
                Move(Path.Combine(stage, name), target);
                moved.Add(name);
            }
        }
        catch (Exception publicationError)
        {
            try
            {
                foreach (var name in moved.AsEnumerable().Reverse())
                    Move(Path.Combine(destination, name), Path.Combine(stage, name));
                foreach (var name in saved.AsEnumerable().Reverse())
                    Move(Path.Combine(backup, name), Path.Combine(destination, name));
            }
            catch (Exception rollbackError)
            {
                File.WriteAllText(Path.Combine(stage, "recovery.required"), publicationError + "\n" + rollbackError);
                throw new AggregateException(
                    $"Publication rollback needs recovery; preserved files at {stage}",
                    publicationError,
                    rollbackError
                );
            }
            throw;
        }
    }

    void Configure(SetupInput s, string id, string? path = null)
    {
        config.Write(
            path ?? Config,
            [
                new(RuntimeConfigKeys.Paths, RuntimeConfigKeys.DvdRoot, RuntimeConfiguration.Format(Path.Combine(s.Workspace, "Assets/DATA"))),
                new(
                    RuntimeConfigKeys.Paths,
                    RuntimeConfigKeys.RetroRewindRoot,
                    RuntimeConfiguration.Format(id == "base" ? "" : Path.Combine(Package, "RetroRewind6"))
                ),
                new(RuntimeConfigKeys.Paths, RuntimeConfigKeys.OverlayRoots, "[]"),
                new(RuntimeConfigKeys.Network, RuntimeConfigKeys.NetworkEnabled, "false"),
            ],
            true
        );
    }

    public RuntimeSettings ReadSettings() => config.ReadSettings(Config);

    public void WriteSettings(RuntimeSettings settings) => config.WriteSettings(Config, settings);

    public async Task<int> Launch(SetupInput s, string id, Action<string, object> emit, CancellationToken ct)
    {
        _ = ProductFor(id);
        if (!(await Status(s, ct)).Single(p => p.Id == id).Ready)
            throw new InvalidOperationException("Product is not ready; rebuild with current inputs");
        Configure(s, id);
        emit("log", new { stage = $"Launching {id}; config={Config}; runtime logs={Path.Combine(Runtime, "UserData/Logs")}" });
        return await ChildProcess.Run(Executable(ProductFor(id)), [], Runtime, (stream, line) => emit("log", new { stage = line, stream }), ct);
    }
}
