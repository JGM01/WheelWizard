using WheelWizard.Core.GitHub;
using WheelWizard.Core.Recomp;

namespace WheelWizard.Core.Test;

public sealed class RecompBoundaryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("diagnostic output")]
    [InlineData("{")]
    [InlineData("{\"type\":\"future-event\"}")]
    public void ParserIgnoresUnknownOrMalformedLines(string? line) => Assert.Null(RecompSetupOutputParser.Parse(line));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"current\"}")]
    [InlineData("{\"status\":\"future-status\",\"detail\":\"new\"}")]
    public void IncompleteOrUnknownProductCannotBeLaunched(string product)
    {
        var line = "{\"type\":\"products\",\"setupVersion\":\"1.0.0\",\"installDir\":\"install\",\"rebuildRequired\":false,\"base\":"
            + product + ",\"retroRewind\":{\"status\":\"current\",\"detail\":\"ok\"}}";
        var report = Assert.IsType<RecompProductsEvent>(RecompSetupOutputParser.Parse(line));
        Assert.True(report.IsBlocked);
        Assert.True(report.ActionRequired);
        Assert.False(report.Base.IsCurrent);
    }

    [Theory]
    [InlineData("\"setupVersion\":null,")]
    [InlineData("\"installDir\":false,")]
    [InlineData("\"rebuildRequired\":\"false\",")]
    public void MissingOrMalformedIdentityIsInvalid(string field)
    {
        var report = Assert.IsType<RecompProductsEvent>(RecompSetupOutputParser.Parse(
            "{\"type\":\"products\"," + field + "\"base\":{\"status\":\"current\",\"detail\":\"ok\"},"
            + "\"retroRewind\":{\"status\":\"current\",\"detail\":\"ok\"}}"));
        Assert.False(report.ProtocolValid);
        Assert.True(report.IsBlocked);
    }

    [Fact]
    public void WindowsDriveRootAndTrailingSeparatorsAreQuotedForSetup()
    {
        Assert.Equal("""--check-products --install-dir "D:\\" --retro-dir "D:\mods" --progress-json""",
            RecompSetupCommandBuilder.BuildCheckProductsArguments(@"D:\", @"D:\mods\\"));
    }

    [Fact]
    public void ReleaseSelectionUsesNewestUsableVersionAndPreservesDtoJson()
    {
        GithubRelease Release(string tag, bool prerelease = false, string? url = "https://example.test/setup") => new()
        {
            TagName = tag,
            Prerelease = prerelease,
            Assets = url is null ? [] : [new() { Name = "wiicompiled-setup.EXE", BrowserDownloadUrl = url }],
        };
        var releases = new[] { Release("v1.9.0"), Release("v2.0.0"), Release("v4.0.0", true), Release("v3.0.0", url: null), Release("bad"), Release("v5.0.0", url: " ") };
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<GithubRelease[]>(System.Text.Json.JsonSerializer.Serialize(releases));
        Assert.Equal("v2.0.0", RecompReleaseResolver.FindLatest(roundTrip)?.TagName);
        Assert.Null(RecompReleaseResolver.FindLatest(null));
        Assert.Null(RecompReleaseResolver.FindLatest([]));
    }

    [Theory]
    [InlineData(" V1.2.3 ", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("bad", null)]
    [InlineData(null, null)]
    public void VersionParsingRetainsExistingTolerance(string? text, string? expected)
    {
        Assert.Equal(expected is not null, RecompVersion.TryParse(text, out var version));
        Assert.Equal(expected, version?.ToString());
    }

    [Fact]
    public async Task ResultConversionsRetainErrorAndTranslationMetadata()
    {
        OperationResult<int> success = 42;
        Assert.Equal(42, success.Value);
        var exception = new IOException("failed");
        var error = OperationError.Fail(exception);
        OperationError.Fail(error, MessageTranslation.Error_ModDownloadFailed, ["title"], ["extra"]);
        OperationResult<int> failed = error;
        Assert.True(failed.IsFailure);
        Assert.Same(error, failed.Error);
        Assert.Same(exception, failed.Error.Exception);
        Assert.Equal("title", failed.Error.TitleReplacements![0]);
        Assert.Equal("extra", failed.Error.ExtraReplacements![0]);
        Assert.Equal(MessageTranslation.Error_ModDownloadFailed, failed.Error.MessageTranslation);
        Assert.Throws<InvalidOperationException>(() => failed.Value);
        var caught = await OperationResult.TryCatch<int>(() => Task.FromException<int>(exception), translation: MessageTranslation.Error_ModDownloadFailed);
        Assert.Same(exception, caught.Error!.Exception);
        Assert.Equal(MessageTranslation.Error_ModDownloadFailed, caught.Error.MessageTranslation);
    }
}
