using System.Diagnostics;

namespace WheelWizard.Host;

public static class ChildProcess
{
    public static async Task<int> Run(
        string executable,
        IEnumerable<string> arguments,
        string directory,
        Action<string, string> log,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null
    )
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments)
            info.ArgumentList.Add(arg);
        info.Environment["PATH"] =
            "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:" + Environment.GetEnvironmentVariable("PATH");
        if (environment != null)
            foreach (var pair in environment)
                info.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = info };
        process.Start();
        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        });
        async Task Drain(StreamReader reader, string stream)
        {
            while (await reader.ReadLineAsync() is { } line)
                log(stream, line);
        }
        await Task.WhenAll(Drain(process.StandardOutput, "stdout"), Drain(process.StandardError, "stderr"), process.WaitForExitAsync());
        ct.ThrowIfCancellationRequested();
        return process.ExitCode;
    }
}
