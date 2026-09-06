using System.Text.Json;
using WheelWizard.Core;
using WheelWizard.Core.GameBanana;
using WheelWizard.Core.Mods;
using WheelWizard.Core.Recomp;
using WheelWizard.Host;

var root =
    Environment.GetEnvironmentVariable("WHEELWIZARD_NATIVE_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support/WheelWizardNative");
var workflow = new ProductWorkflow(root, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../tools")));
var mods = new ModLibrary(Path.Combine(root, "Mods"));
// Tests point this at a local stub so the offline bridge suite never needs GameBanana.
var gameBananaBaseUrl = Environment.GetEnvironmentVariable("WHEELWIZARD_GAMEBANANA_URL");
var gameBananaHttp = new HttpClient();
var gameBanana = new GameBananaCatalog(gameBananaHttp, gameBananaBaseUrl);
Directory.CreateDirectory(workflow.Logs);
var outputLock = new object();
void Send(HostEvent value)
{
    lock (outputLock)
        Console.WriteLine(JsonSerializer.Serialize(value, ProductWorkflow.Json));
}
CancellationTokenSource? active = null;
var activeLock = new object();
void CancelActive() { lock (activeLock) active?.Cancel(); }
Task running = Task.CompletedTask;
int occupied = 0;
using var sessionLock = new FileStream(Path.Combine(root, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
using var shutdown = new CancellationTokenSource();
using var signal = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM,
    context =>
    {
        context.Cancel = true;
        shutdown.Cancel();
        CancelActive();
    }
);
var choiceLock = new object();
(string Id, TaskCompletionSource<bool> Answer)? pendingChoice = null;
object ModRow(ModMetadata mod, CancellationToken ct)
{
    IReadOnlyList<ModCompatibilityFinding> findings = [];
    string? inspectionError = null;
    try { findings = ModCompatibility.Scan(mods.DirectoryFor(mod.Title), mod.Title, ct); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    { inspectionError = error.Message; }
    // A failed inspection must not hide the mod or make it impossible to disable/remove it.
    return new { mod.Title, mod.Author, mod.ModID, mod.IsEnabled, mod.Priority, findings, inspectionError };
}
object ModList(CancellationToken ct) => new { mods = mods.Load(ct).Select(mod => ModRow(mod, ct)).ToArray() };
var seen = new HashSet<string>();
try
{
    while (await Task.Run(Console.In.ReadLine).WaitAsync(shutdown.Token) is { } line)
    {
        Request? request = null;
        try
        {
            request = JsonSerializer.Deserialize<Request>(line, ProductWorkflow.Json) ?? throw new FormatException("Empty request");
            if (request.Version != 1 || string.IsNullOrWhiteSpace(request.Id))
                throw new FormatException("Protocol version 1 and request ID required");
            if (!seen.Add(request.Id))
                throw new FormatException("Duplicate request ID");
            if (request.Command == "cancel")
            {
                CancelActive();
                Send(new(1, request.Id, "result", Outcome: "success"));
                continue;
            }
            if (request.Command == "launch-choice")
            {
                lock (choiceLock)
                {
                    if (pendingChoice is not { } choice || choice.Id != request.LaunchId || request.Choice is not ("delete" or "keep"))
                        throw new ArgumentException("No matching launch choice is pending");
                    pendingChoice = null;
                    Send(new(1, request.Id, "result", Outcome: "success"));
                    choice.Answer.TrySetResult(request.Choice == "delete");
                }
                continue;
            }
            if (Interlocked.CompareExchange(ref occupied, 1, 0) != 0)
            {
                Send(new(1, request.Id, "result", Outcome: "failure", Error: "An operation is already active"));
                continue;
            }
            CancellationToken ct;
            lock (activeLock)
            {
                active?.Dispose();
                active = new CancellationTokenSource();
                ct = active.Token;
            }
            var req = request;
            running = Task.Run(async () =>
            {
                var logPath = Path.Combine(
                    workflow.Logs,
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".jsonl"
                );
                StreamWriter? log = null;
                object logLock = new();
                void Record(HostEvent ev)
                {
                    try { lock (logLock) log?.WriteLine(JsonSerializer.Serialize(ev, ProductWorkflow.Json)); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { Console.Error.WriteLine("Operation log write failed: " + error.Message); }
                }
                void Emit(string kind, object data)
                {
                    var ev = new HostEvent(1, req.Id, kind, data);
                    Record(ev);
                    Send(ev);
                }
                void Complete(HostEvent ev)
                {
                    Record(ev);
                    lock (outputLock)
                    {
                        Interlocked.Exchange(ref occupied, 0);
                        Send(ev);
                    }
                }
                async Task<bool> ChoosePatches(CancellationToken token)
                {
                    var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (choiceLock) pendingChoice = (req.Id, answer);
                    try
                    {
                        Emit("phase", new { phase = "awaiting-choice" });
                        return await answer.Task.WaitAsync(token);
                    }
                    finally
                    {
                        lock (choiceLock)
                            if (pendingChoice?.Id == req.Id) pendingChoice = null;
                    }
                }
                try
                {
                    log = new StreamWriter(logPath) { AutoFlush = true };
                    Emit("log", new { stage = $"{req.Command}; operation log: {logPath}" });
                    var setup = workflow.Discover(req.Setup ?? new());
                    object? result;
                    switch (req.Command)
                    {
                        case "mods-list":
                            result = ModList(ct);
                            break;
                        case "mods-import":
                            await mods.ImportNext(
                                req.ArchivePath ?? throw new ArgumentException("archivePath required"),
                                req.ModTitle ?? throw new ArgumentException("modTitle required"),
                                progress: new ModEventProgress(update => Emit("progress", new { stage = update.Stage, percent = update.Percent })),
                                ct: ct
                            );
                            result = ModList(CancellationToken.None);
                            break;
                        case "mods-enabled":
                            mods.SetEnabled(
                                req.ModTitle ?? throw new ArgumentException("modTitle required"),
                                req.Enabled ?? throw new ArgumentException("enabled required")
                            );
                            result = ModList(CancellationToken.None);
                            break;
                        case "mods-move":
                            mods.Move(
                                req.ModTitle ?? throw new ArgumentException("modTitle required"),
                                req.Direction ?? throw new ArgumentException("direction required")
                            );
                            result = ModList(CancellationToken.None);
                            break;
                        case "mods-reorder":
                            mods.Reorder(req.Titles ?? throw new ArgumentException("titles required"));
                            result = ModList(CancellationToken.None);
                            break;
                        case "mods-remove":
                            mods.Remove(req.ModTitle ?? throw new ArgumentException("modTitle required"));
                            result = ModList(CancellationToken.None);
                            break;
                        case "mods-preview":
                            result = ModLaunchPlanner.Build(mods.Root, mods.Load(ct), ct);
                            break;
                        case "mods-search":
                            result = ModSearchProjection(
                                RequireResult(await gameBanana.GetModSearchResults(req.Search ?? "", req.Page ?? 1, ct))
                            );
                            break;
                        case "mods-details":
                            result = ModDetailsProjection(
                                RequireResult(
                                    await gameBanana.GetModDetails(req.ModId ?? throw new ArgumentException("modId required"), ct)
                                )
                            );
                            break;
                        case "mods-install":
                        {
                            var modTitle = req.ModTitle ?? throw new ArgumentException("modTitle required");
                            var uri = new Uri(req.Url ?? throw new ArgumentException("url required"));
                            ValidateModDownloadUrl(uri, gameBananaBaseUrl);
                            var downloads = Path.Combine(mods.Root, ".downloads");
                            Directory.CreateDirectory(downloads);
                            var dest = Path.Combine(
                                downloads,
                                Guid.NewGuid().ToString("N") + Path.GetExtension(uri.AbsolutePath)
                            );
                            try
                            {
                                await HttpDownloads.ToFileAsync(
                                    gameBananaHttp,
                                    uri,
                                    dest,
                                    percent => Emit("progress", new { stage = "download", percent }),
                                    ct
                                );
                                // GameBanana download links often carry no filename; sniff the real container type.
                                var detected = DetectArchiveExtension(dest);
                                if (detected != null && !string.Equals(Path.GetExtension(dest), detected, StringComparison.OrdinalIgnoreCase))
                                {
                                    var renamed = dest + detected;
                                    File.Move(dest, renamed);
                                    dest = renamed;
                                }
                                await mods.ImportNext(
                                    dest,
                                    modTitle,
                                    req.Author ?? "-1",
                                    req.ModId ?? -1,
                                    new ModEventProgress(update => Emit("progress", new { stage = update.Stage, percent = update.Percent })),
                                    ct
                                );
                            }
                            finally
                            {
                                if (File.Exists(dest))
                                    File.Delete(dest);
                            }
                            result = ModList(CancellationToken.None);
                            break;
                        }
                        case "preflight":
                            result = new { setup, errors = workflow.Preflight(setup) };
                            break;
                        case "status":
                            result = new
                            {
                                products = await workflow.Status(setup, ct),
                                package = workflow.PackageStatus(),
                                recovery = workflow.Patches.Recovery,
                                logs = workflow.Logs,
                                runtimeLogs = Path.Combine(workflow.Runtime, "UserData/Logs"),
                            };
                            break;
                        case "package-status":
                            result = workflow.PackageStatus();
                            break;
                        case "patches-restore":
                            workflow.Patches.Restore();
                            result = new { recovery = workflow.Patches.Recovery };
                            break;
                        case "package-latest":
                            using (var http = new HttpClient())
                                result = new { version = await new WheelWizard.Core.RetroRewindPackage(http).LatestAsync(ct) };
                            break;
                        case "install":
                            await workflow.Install(setup, Emit, ct);
                            result = workflow.PackageStatus();
                            break;
                        case "package-update":
                            await workflow.UpdateRR(setup, Emit, ct);
                            result = new { products = await workflow.Status(setup, ct), package = workflow.PackageStatus() };
                            break;
                        case "package-remove":
                            workflow.RemoveRR();
                            result = new { products = await workflow.Status(setup, ct), package = workflow.PackageStatus() };
                            break;
                        case "package-state":
                            result = await workflow.PackageState(ct);
                            break;
                        case "build":
                            await workflow.Build(setup, req.Product ?? "", Emit, ct);
                            result = new { products = await workflow.Status(setup, CancellationToken.None) };
                            break;
                        case "config-read":
                            result = workflow.ReadSettings();
                            break;
                        case "config-write":
                            workflow.WriteSettings(req.Settings ?? throw new ArgumentException("settings required"));
                            result = workflow.ReadSettings();
                            break;
                        case "launch":
                            var exit = await workflow.Launch(setup, req.Product ?? "", Emit, ct, ChoosePatches);
                            Emit("status", new { exitCode = exit });
                            if (exit != 0)
                                throw new InvalidOperationException($"Game exited with code {exit}");
                            result = new
                            {
                                exitCode = exit,
                                settings = workflow.ReadSettings(),
                                products = await workflow.Status(setup, ct),
                            };
                            break;
                        default:
                            throw new ArgumentException("Unknown command: " + req.Command);
                    }
                    Complete(new(1, req.Id, "result", result, "success"));
                }
                catch (OperationCanceledException)
                {
                    Complete(new(1, req.Id, "result", Outcome: "cancelled"));
                }
                catch (Exception e)
                {
                    if (e is ModCompatibilityException incompatible)
                        Emit("blockers", new { findings = incompatible.Findings });
                    Emit("recovery", new { recovery = workflow.Patches.Recovery });
                    Console.Error.WriteLine(e);
                    Complete(new(1, req.Id, "result", Outcome: "failure", Error: e.Message));
                }
                finally
                {
                    try { log?.Dispose(); }
                    catch (IOException e) { Console.Error.WriteLine(e.Message); }
                }
            });
        }
        catch (Exception e)
        {
            Send(new(1, request?.Id ?? "", "result", Outcome: "failure", Error: e.Message));
        }
    }
}
catch (OperationCanceledException) { }
CancelActive();
await running;
lock (activeLock) active?.Dispose();

// Slim, Swift-friendly projections of the GameBanana catalog (see macos/Native README). Only the
// fields the native browser renders travel over the protocol; image URLs are absolute here.
static object? ModSearchProjection(GameBananaSearchResults value) =>
    new
    {
        recordCount = value.MetaData.RecordCount,
        perPage = value.MetaData.PerPage,
        isComplete = value.MetaData.IsComplete,
        results = value
            .Records.Where(mod => mod.ModelName == "Mod" && !mod.HasContentRatings)
            .Select(mod => new
            {
                id = mod.Id,
                name = mod.Name,
                version = mod.Version,
                author = mod.Author.Name,
                profileUrl = mod.ProfileUrl,
                imageUrl = mod.PreviewMedia?.Images.FirstOrDefault() is { } image ? image.BaseUrl + "/" + image.File : null,
                likeCount = mod.LikeCount,
                viewCount = mod.ViewCount,
                usesPatches = mod.UsesPatches,
                tags = mod.Tags.Select(tag => tag.Title).ToArray(),
            }),
    };

static object? ModDetailsProjection(GameBananaModDetails mod) =>
    new
    {
        id = mod.Id,
        name = mod.Name,
        version = mod.Version,
        profileUrl = mod.ProfileUrl,
        author = new { name = mod.Author.Name, profileUrl = mod.Author.ProfileUrl },
        likeCount = mod.LikeCount,
        viewCount = mod.ViewCount,
        downloadCount = mod.DownloadCount,
        text = mod.Text,
        images = (mod.PreviewMedia?.Images ?? []).Select(image => image.BaseUrl + "/" + image.File).ToArray(),
        files = (mod.Files ?? []).Select(ModFileProjection).ToArray(),
        archivedFiles = (mod.ArchivedFiles ?? []).Select(ModFileProjection).ToArray(),
    };

static object ModFileProjection(GameBananaModFiles file) =>
    new { fileName = file.FileName, fileSize = file.FileSize, downloadUrl = file.DownloadUrl };

// OperationResult carries the real failure message; throw it instead of tripping OperationResult<T>.Value's
// generic "The operation was not successful." so the caller sees what actually went wrong.
static T RequireResult<T>(OperationResult<T> result) =>
    result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error.Message);

static void ValidateModDownloadUrl(Uri uri, string? overrideBaseUrl)
{
    // Tests override the catalog base with a local http:// stub; allow downloads only from that host then.
    var overrideUri = overrideBaseUrl is null ? null : new Uri(overrideBaseUrl);
    var matchesOverride =
        overrideUri != null
        && uri.Scheme == overrideUri.Scheme
        && uri.Host == overrideUri.Host
        && uri.Port == overrideUri.Port;
    if (!matchesOverride && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Mod download URL must use HTTPS.");
}

// GameBanana file download links are usually /dl/{id} with no extension; the container decides.
static string? DetectArchiveExtension(string path)
{
    Span<byte> head = stackalloc byte[6];
    using var stream = File.OpenRead(path);
    var read = stream.Read(head);
    if (read >= 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4)
        return ".zip";
    if (read >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
        return ".7z";
    if (read >= 4 && head[0] == (byte)'R' && head[1] == (byte)'a' && head[2] == (byte)'r' && head[3] == (byte)'!')
        return ".rar";
    return null;
}

// Synchronous reporting keeps progress events before the terminal result.
sealed class ModEventProgress(Action<ModProgress> report) : IProgress<ModProgress>
{
    public void Report(ModProgress value) => report(value);
}
