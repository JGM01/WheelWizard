using System.IO.Compression;
using Semver;

namespace WheelWizard.Core;

public record PackageProgress(string Stage, double? Percent);

public record StagedPackage(string Path, string Version);

public sealed class RetroRewindPackage(HttpClient http)
{
    public const string Endpoint = "https://update.rwfc.net/RetroRewind/";

    public static string? InstalledVersion(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), @"^\d+\.\d+\.\d+$") && SemVersion.TryParse(text.Trim(), out var v)
            ? v.ToString()
            : null;

    public static string LatestVersion(string text) =>
        SemVersion
            .Parse(
                text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Last()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
            )
            .ToString();

    public static Uri ResolveUrl(string text)
    {
        var uri = new Uri(text.Trim().Replace("http://update.rwfc.net:8000/", "https://update.rwfc.net/"), UriKind.Absolute);
        if (uri.Scheme != "https")
            throw new InvalidDataException("Package URL must use HTTPS");
        return uri;
    }

    public async Task<string> LatestAsync(CancellationToken ct) =>
        LatestVersion(await http.GetStringAsync(Endpoint + "RetroRewindVersion.txt", ct));

    public async Task<StagedPackage> StageAsync(string stagingParent, IProgress<PackageProgress>? progress, CancellationToken ct)
    {
        var url = ResolveUrl(await http.GetStringAsync(Endpoint + "RetroRewindInstall.txt", ct));
        var stage = System.IO.Path.Combine(stagingParent, "rr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var zip = System.IO.Path.Combine(stage, "package.zip");
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(zip);
                long lastProgress = 0;
                var buffer = new byte[131072];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    total += count;
                    if (Environment.TickCount64 - lastProgress >= 100)
                    {
                        lastProgress = Environment.TickCount64;
                        progress?.Report(
                            new(
                                "download",
                                response.Content.Headers.ContentLength is > 0
                                    ? total * 100d / response.Content.Headers.ContentLength.Value
                                    : null
                            )
                        );
                    }
                }
            }
            var extracted = System.IO.Path.Combine(stage, "content");
            Extract(zip, extracted, progress, ct);
            File.Delete(zip);
            return new(stage, Validate(extracted));
        }
        catch
        {
            Directory.Delete(stage, true);
            throw;
        }
    }

    public static string Validate(string root)
    {
        foreach (var file in new[] { "RetroRewind6/version.txt", "RetroRewind6/Binaries/Code.pul", "riivolution/RetroRewind6.xml" })
            if (!File.Exists(System.IO.Path.Combine(root, file)))
                throw new InvalidDataException($"Package is missing {file}");
        return InstalledVersion(File.ReadAllText(System.IO.Path.Combine(root, "RetroRewind6/version.txt")))
            ?? throw new InvalidDataException("Invalid installed RR version");
    }

    public static void Extract(string archivePath, string destination, IProgress<PackageProgress>? progress, CancellationToken ct)
    {
        var root = System.IO.Path.GetFullPath(destination) + System.IO.Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var archive = ZipFile.OpenRead(archivePath);
        int i = 0;
        long lastProgress = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, name));
            if (
                !path.StartsWith(root, StringComparison.Ordinal)
                || name.Split('/').Contains("..")
                || name.Contains(':')
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
            )
                throw new InvalidDataException($"Unsafe archive path: {name}");
            if (name.EndsWith('/'))
                Directory.CreateDirectory(path);
            else if (!name.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using var input = entry.Open();
                using var output = File.Create(path);
                var buffer = new byte[131072];
                int count;
                while ((count = input.Read(buffer)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count);
                }
            }
            i++;
            if (Environment.TickCount64 - lastProgress >= 100 || i == archive.Entries.Count)
            {
                lastProgress = Environment.TickCount64;
                progress?.Report(new("extract", i * 100d / archive.Entries.Count));
            }
        }
    }
}
