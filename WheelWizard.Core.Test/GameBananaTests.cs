using System.Net;
using System.Text.Json;
using WheelWizard.Core.GameBanana;

namespace WheelWizard.Core.Test;

public sealed class GameBananaTests
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    static GameBananaCatalog Catalog(HttpMessageHandler handler) => new(new HttpClient(handler));

    // A minimal but complete search record; tags may be strings and/or {_sTitle,_sValue} objects.
    static string Preview(int id, string name, string tagsJson, string modelName = "Mod", bool contentRatings = false) =>
        $$"""
        {
          "_idRow": {{id}},
          "_sName": "{{name}}",
          "_sVersion": "1.0",
          "_aTags": {{tagsJson}},
          "_sProfileUrl": "https://gamebanana.com/mods/{{id}}",
          "_aPreviewMedia": { "_aImages": [ { "_sType": "ss", "_sBaseUrl": "https://images.example.test", "_sFile": "img/{{id}}.png", "_sFile220": "img/{{id}}_220.png" } ] },
          "_bHasContentRatings": {{contentRatings.ToString().ToLowerInvariant()}},
          "_nLikeCount": 12,
          "_nViewCount": 340,
          "_tsDateAdded": 1609459200,
          "_tsDateModified": 1609545600,
          "_aSubmitter": { "_sName": "Author {{id}}", "_sProfileUrl": "https://gamebanana.com/members/{{id}}", "_sAvatarUrl": null },
          "_aGame": { "_sName": "Mario Kart Wii", "_sProfileUrl": "https://gamebanana.com/games/5896", "_sIconUrl": "" },
          "_aRootCategory": { "_sName": "Maps", "_sProfileUrl": "", "_sIconUrl": null },
          "_sModelName": "{{modelName}}"
        }
        """;

    static string SearchJson(string recordCount, string recordsJson, bool isComplete) =>
        $$"""
        {
          "_aMetadata": { "_nRecordCount": {{recordCount}}, "_nPerpage": 10, "_bIsComplete": {{isComplete.ToString().ToLowerInvariant()}} },
          "_aRecords": [ {{recordsJson}} ]
        }
        """;

    [Fact]
    public async Task SearchSendsQueryPageAndDefaultsTerm()
    {
        Uri? requested = null;
        var catalog = Catalog(new Handler((request, _) =>
        {
            requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SearchJson("1", Preview(7, "Mushroom Cup", "[]"), true)),
            });
        }));

        var withTerm = await catalog.GetModSearchResults("track");
        Assert.True(withTerm.IsSuccess);
        Assert.Equal(7, withTerm.Value.Records.Single().Id);
        Assert.Contains("_sSearchString=track", requested!.Query);
        Assert.Contains("_idGameRow=5896", requested.Query);
        Assert.Contains("_sModelName=Mod", requested.Query);
        Assert.Contains("_nPage=1", requested.Query);
        Assert.StartsWith("https://gamebanana.com/apiv12/Util/Search/Results", requested!.AbsoluteUri);
        Assert.Contains("_idGameRow=5896", requested.Query);

        requested = null;
        await catalog.GetModSearchResults("", 3);
        Assert.Contains("_sSearchString=Mod", requested!.Query);
        Assert.Contains("_nPage=3", requested.Query);
    }

    [Fact]
    public async Task SearchParsesStringAndObjectTagsAndFlagsPatches()
    {
        var tags = """["patch", {"_sTitle":"Characters","_sValue":"mario"}, {"_sTitle":"Sound","_sValue":"music"}]""";
        var record = Preview(1, "Patched", tags);
        var catalog = Catalog(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SearchJson("1", record, true)),
        })));

        var result = await catalog.GetModSearchResults("x");
        var mod = Assert.Single(result.Value.Records);
        Assert.Equal(3, mod.Tags.Count);
        Assert.True(mod.UsesPatches);
        Assert.False(mod.HasContentRatings);
        var image = mod.PreviewMedia!.Images[0];
        Assert.Equal("https://images.example.test", image.BaseUrl);
        Assert.Equal("img/1.png", image.File);
    }

    [Fact]
    public async Task DetailsMapsFilesTextAndArchivedFiles()
    {
        const string detailsJson =
            """
            {
              "_idRow": 42,
              "_sName": "Full Course",
              "_sVersion": "2.1",
              "_sProfileUrl": "https://gamebanana.com/mods/42",
              "_aPreviewMedia": { "_aImages": [] },
              "_nLikeCount": 5,
              "_nViewCount": 60,
              "_tsDateAdded": 1,
              "_tsDateModified": 2,
              "_bIsObsolete": false,
              "_aSubmitter": { "_sName": "Builder", "_sProfileUrl": "https://gamebanana.com/members/9", "_sAvatarUrl": "" },
              "_aGame": { "_sName": "Mario Kart Wii", "_sProfileUrl": "", "_sIconUrl": "" },
              "_aCategory": { "_sName": "Maps", "_sProfileUrl": "", "_sIconUrl": "" },
              "_aSuperCategory": null,
              "_sText": "<p>Description</p>",
              "_sLicense": "MIT",
              "_aLicenseCheckList": null,
              "_nDownloadCount": 99,
              "_aFiles": [ { "_sFile": "full-course.zip", "_nFilesize": 4096, "_sDownloadUrl": "https://gamebanana.com/dl/42" } ],
              "_aArchivedFiles": [ { "_sFile": "full-course-old.zip", "_nFilesize": 100, "_sDownloadUrl": "https://gamebanana.com/dl/42-old" } ]
            }
            """;
        var catalog = Catalog(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(detailsJson),
        })));

        var result = await catalog.GetModDetails(42);
        var mod = Assert.IsType<GameBananaModDetails>(result.Value);
        Assert.Equal("Full Course", mod.Name);
        Assert.Equal("<p>Description</p>", mod.Text);
        var file = Assert.Single(mod.Files!);
        Assert.Equal("full-course.zip", file.FileName);
        Assert.Equal("https://gamebanana.com/dl/42", file.DownloadUrl);
        Assert.Equal("full-course-old.zip", Assert.Single(mod.ArchivedFiles!).FileName);
        Assert.Equal("Builder", mod.Author.Name);
    }

    [Fact]
    public async Task HttpFailureReturnsOperationFailure()
    {
        var catalog = Catalog(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var result = await catalog.GetModDetails(1);
        Assert.True(result.IsFailure);
        Assert.Contains("GameBanana request failed", result.Error.Message);
    }

    [Fact]
    public async Task CancellationIsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        var catalog = Catalog(new Handler((_, ct) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(ct);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetModSearchResults("x", ct: cancellation.Token));
    }

    [Fact]
    public async Task SearchResultsStayIncludingContentRatingsAndOtherModels()
    {
        var records = string.Join(
            ",",
            Preview(1, "Modded", """[]"""),
            Preview(2, "Question", """[]""", modelName: "Question"),
            Preview(3, "Rated", """[]""", contentRatings: true)
        );
        var catalog = Catalog(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SearchJson("3", records, true)),
        })));
        var result = await catalog.GetModSearchResults("x");
        // The catalog is a plain transport; the "Mod only, no content ratings" filter is a caller concern.
        Assert.Equal(3, result.Value.Records.Count);
        Assert.Contains(result.Value.Records, r => r.HasContentRatings);
        Assert.Contains(result.Value.Records, r => r.ModelName == "Question");
    }
}
