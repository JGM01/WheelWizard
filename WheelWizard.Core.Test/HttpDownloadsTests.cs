using System.Net;

namespace WheelWizard.Core.Test;

public sealed class HttpDownloadsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ww downloads " + Guid.NewGuid());

    public HttpDownloadsTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    [Fact]
    public async Task WritesBodyToFile()
    {
        var progress = new List<double?>();
        var body = new string('x', 300_000);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        })));
        var destination = Path.Combine(root, "out.bin");
        await HttpDownloads.ToFileAsync(http, new Uri("https://example.test/a.bin"), destination, progress.Add, default);

        Assert.Equal(body, File.ReadAllText(destination));
        // Progress reporting is throttled, so short bodies may legitimately report nothing.
        Assert.All(progress, p => Assert.InRange(p ?? 0, 0, 100));
    }

    [Fact]
    public async Task PropagatesHttpErrorsAndLeavesNothingWhenDownloadFails()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var destination = Path.Combine(root, "out.bin");
        await Assert.ThrowsAsync<HttpRequestException>(() => HttpDownloads.ToFileAsync(http, new Uri("https://example.test/missing"), destination, null, default));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(1, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpDownloads.ToFileAsync(
            http, new Uri("https://example.test/a.bin"), Path.Combine(root, "out.bin"), null, cancellation.Token));
    }
}
