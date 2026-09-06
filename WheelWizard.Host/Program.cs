using System.Text.Json;
using WheelWizard.Core.Recomp;
using WheelWizard.Host;

var root =
    Environment.GetEnvironmentVariable("WHEELWIZARD_NATIVE_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support/WheelWizardNative");
var workflow = new ProductWorkflow(root, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../tools")));
Directory.CreateDirectory(workflow.Logs);
var outputLock = new object();
void Send(HostEvent value)
{
    lock (outputLock)
        Console.WriteLine(JsonSerializer.Serialize(value, ProductWorkflow.Json));
}
CancellationTokenSource? active = null;
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
        active?.Cancel();
    }
);
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
                active?.Cancel();
                Send(new(1, request.Id, "result", Outcome: "success"));
                continue;
            }
            if (Interlocked.CompareExchange(ref occupied, 1, 0) != 0)
            {
                Send(new(1, request.Id, "result", Outcome: "failure", Error: "An operation is already active"));
                continue;
            }
            active?.Dispose();
            active = new CancellationTokenSource();
            var ct = active.Token;
            var req = request;
            running = Task.Run(async () =>
            {
                var logPath = Path.Combine(
                    workflow.Logs,
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".jsonl"
                );
                using var log = new StreamWriter(logPath) { AutoFlush = true };
                object logLock = new();
                void Emit(string kind, object data)
                {
                    var ev = new HostEvent(1, req.Id, kind, data);
                    lock (logLock)
                        log.WriteLine(JsonSerializer.Serialize(ev, ProductWorkflow.Json));
                    Send(ev);
                }
                try
                {
                    Emit("log", new { stage = $"{req.Command}; operation log: {logPath}" });
                    var setup = workflow.Discover(req.Setup ?? new());
                    object? result;
                    switch (req.Command)
                    {
                        case "preflight":
                            result = new { setup, errors = workflow.Preflight(setup) };
                            break;
                        case "status":
                            result = new
                            {
                                products = await workflow.Status(setup, ct),
                                package = workflow.PackageStatus(),
                                logs = workflow.Logs,
                                runtimeLogs = Path.Combine(workflow.Runtime, "UserData/Logs"),
                            };
                            break;
                        case "package-status":
                            result = workflow.PackageStatus();
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
                            result = new
                            {
                                products = await workflow.Status(setup, ct),
                                package = workflow.PackageStatus(),
                            };
                            break;
                        case "package-remove":
                            workflow.RemoveRR();
                            result = new
                            {
                                products = await workflow.Status(setup, ct),
                                package = workflow.PackageStatus(),
                            };
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
                            var exit = await workflow.Launch(setup, req.Product ?? "", Emit, ct);
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
                    lock (logLock)
                        log.WriteLine(JsonSerializer.Serialize(new HostEvent(1, req.Id, "result", result, "success"), ProductWorkflow.Json));
                    Interlocked.Exchange(ref occupied, 0);
                    Send(new(1, req.Id, "result", result, "success"));
                }
                catch (OperationCanceledException)
                {
                    lock (logLock)
                        log.WriteLine(JsonSerializer.Serialize(new HostEvent(1, req.Id, "result", Outcome: "cancelled"), ProductWorkflow.Json));
                    Interlocked.Exchange(ref occupied, 0);
                    Send(new(1, req.Id, "result", Outcome: "cancelled"));
                }
                catch (Exception e)
                {
                    lock (logLock)
                        log.WriteLine(
                            JsonSerializer.Serialize(
                                new HostEvent(1, req.Id, "result", Outcome: "failure", Error: e.ToString()),
                                ProductWorkflow.Json
                            )
                        );
                    Console.Error.WriteLine(e);
                    Interlocked.Exchange(ref occupied, 0);
                    Send(new(1, req.Id, "result", Outcome: "failure", Error: e.Message));
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
active?.Cancel();
await running;
active?.Dispose();
