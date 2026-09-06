using System.IO.Abstractions;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WheelWizard.Core.Recomp;

namespace WheelWizard.Core.Test;

public sealed class RecompIoTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ww setup " + Guid.NewGuid());
    private string Target => Path.Combine(root, "setup.exe");

    public RecompIoTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChildCallbackFailureStopsOwnedProcess(bool onStart)
    {
        if (OperatingSystem.IsWindows()) return;
        await Assert.ThrowsAsync<IOException>(async () => await ChildProcess.Run("/bin/bash",
            ["-c", "echo ready; sleep 120"], root,
            (_, _) => { if (!onStart) throw new IOException("Lost output transport"); }, default,
            started: () => { if (onStart) throw new IOException("Lost start notification"); }).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadPublishesCompletedFileAndReportsProgress(bool knownLength)
    {
        File.WriteAllText(Target, "previous executable");
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength ? new StringContent("replacement") : new StreamContent(new UnseekableStream("replacement")),
        })));
        var progress = new ProgressRecorder();
        var result = await Downloader(client).DownloadAsync("https://example.test/setup", Target, progress);
        Assert.True(result.IsSuccess);
        Assert.Equal("replacement", File.ReadAllText(Target));
        Assert.False(File.Exists(Target + ".part"));
        Assert.Equal(100, progress.Values.Last());
        Assert.All(progress.Values, percent => Assert.InRange(percent, 0, 100));
        if (!knownLength)
            Assert.Single(progress.Values);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("stream")]
    [InlineData("timeout")]
    public async Task DownloadFailureKeepsPreviousExecutableAndCleansPartial(string failure)
    {
        File.WriteAllText(Target, "previous");
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(
            failure == "http" ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream(failure == "timeout"
                ? new TaskCanceledException("body timeout") : new IOException("stream failed"))),
        })));
        var result = await Downloader(client).DownloadAsync("https://example.test/setup", Target);
        Assert.True(result.IsFailure);
        if (failure == "timeout")
            Assert.Equal("The download timed out after 15 minutes.", result.Error.Message);
        Assert.Equal("previous", File.ReadAllText(Target));
        Assert.False(File.Exists(Target + ".part"));
    }

    [Fact]
    public async Task CallerCancellationDuringBodyReadPropagatesAndCleansPartial()
    {
        File.WriteAllText(Target, "previous");
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream(new OperationCanceledException(cancellation.Token), cancellation.Cancel)),
        })));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader(client)
            .DownloadAsync("https://example.test/setup", Target, cancellationToken: cancellation.Token));
        Assert.Equal("previous", File.ReadAllText(Target));
        Assert.False(File.Exists(Target + ".part"));
    }

    [Fact]
    public async Task FailedPublicationDoesNotDeletePreviousExecutable()
    {
        File.WriteAllText(Target, "previous");
        // Delegate actual reads/writes to disk, but inject a failure at the publication boundary.
        var files = Substitute.For<IFileSystem>();
        var file = Substitute.For<IFile>();
        files.File.Returns(file);
        var real = new Testably.Abstractions.RealFileSystem();
        files.Path.Returns(real.Path);
        files.Directory.Returns(real.Directory);
        file.Create(Arg.Any<string>()).Returns(call => real.File.Create(call.Arg<string>()));
        file.Exists(Arg.Any<string>()).Returns(call => real.File.Exists(call.Arg<string>()));
        file.When(f => f.Delete(Arg.Any<string>())).Do(call => real.File.Delete(call.Arg<string>()));
        file.When(f => f.Move(Arg.Any<string>(), Arg.Any<string>(), true)).Do(_ => throw new IOException("publication failed"));
        // Also reject the old two-argument path: this test must catch delete-then-move regressions.
        file.When(f => f.Move(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new IOException("publication failed"));
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("replacement"),
        })));
        var result = await Downloader(client, files).DownloadAsync("https://example.test/setup", Target);
        Assert.True(result.IsFailure);
        Assert.Equal("previous", File.ReadAllText(Target));
        Assert.False(File.Exists(Target + ".part"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProbeCachesBothOutcomesAndDoesNotReadBody(bool success)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            Assert.Equal(RecompRetroWfcPayloadProbe.PayloadUri, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(success ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new StreamContent(new FailingStream(new IOException("must not read payload"))),
            });
        }));
        var probe = new RecompRetroWfcPayloadProbe(new Factory(client), NullLogger<RecompRetroWfcPayloadProbe>.Instance);
        Assert.Equal(success, await probe.IsReachableAsync());
        Assert.Equal(success, await probe.IsReachableAsync());
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeDistinguishesCallerCancellationFromTimeout(bool callerCancelled)
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler((_, _) =>
        {
            if (callerCancelled)
                cancellation.Cancel();
            return Task.FromException<HttpResponseMessage>(new TaskCanceledException());
        }));
        var probe = new RecompRetroWfcPayloadProbe(new Factory(client), NullLogger<RecompRetroWfcPayloadProbe>.Instance);
        if (callerCancelled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.IsReachableAsync(cancellation.Token));
        else
            Assert.False(await probe.IsReachableAsync(cancellation.Token));
    }

    private static RecompSetupDownloader Downloader(HttpClient client, IFileSystem? files = null) =>
        new(new Factory(client), files ?? new Testably.Abstractions.RealFileSystem(), NullLogger<RecompSetupDownloader>.Instance);

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(RecompSetupDownloader.HttpClientName, name);
            return client;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class ProgressRecorder : IProgress<int>
    {
        public List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }

    private sealed class UnseekableStream(string text) : MemoryStream(System.Text.Encoding.UTF8.GetBytes(text))
    {
        public override bool CanSeek => false;
    }

    private sealed class FailingStream(Exception failure, Action? beforeFailure = null) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            beforeFailure?.Invoke();
            return ValueTask.FromException<int>(failure);
        }
    }
}
