namespace WheelWizard.Core;

/// <summary>
/// Shared HTTP download primitive used by every Core downloader that streams a body to a local file
/// with optional progress (Retro Rewind package staging, incremental updates, and mod downloads).
/// Callers own staging, cleanup and any higher-level progress wording.
/// </summary>
public static class HttpDownloads
{
    const int BufferSize = 131072;

    // Reporting on every read floods the UI; ~100ms keeps progress smooth without the overhead.
    const long ProgressIntervalMillis = 100;

    /// <summary>
    /// Streams a GET body to <paramref name="destinationPath"/>, reporting 0-100 percent when the
    /// server advertised a content length (otherwise null). Progress is throttled, so no final 100%
    /// report is guaranteed.
    /// </summary>
    public static async Task ToFileAsync(
        HttpClient http,
        Uri url,
        string destinationPath,
        Action<double?>? onProgress = null,
        CancellationToken ct = default
    )
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var contentLength = response.Content.Headers.ContentLength;

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(destinationPath);
        var buffer = new byte[BufferSize];
        long lastProgress = 0;
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            total += count;
            if (Environment.TickCount64 - lastProgress >= ProgressIntervalMillis)
            {
                lastProgress = Environment.TickCount64;
                onProgress?.Invoke(contentLength is > 0 ? total * 100d / contentLength.Value : null);
            }
        }
    }
}
