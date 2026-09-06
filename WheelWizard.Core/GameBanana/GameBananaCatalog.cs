using System.Text.Json;

namespace WheelWizard.Core.GameBanana;

/// <summary>
/// Frontend-agnostic client for the GameBanana API (apiv12). Search covers the Mario Kart Wii
/// (id <see cref="MarioKartWiiGameId"/>) "Mod" catalog; details cover a single mod's profile page.
/// Callers own the <see cref="HttpClient"/> so each host can reuse its transport (and tests can fake it).
/// </summary>
public sealed class GameBananaCatalog
{
    /// <summary>The GameBanana API base address (override for tests/offline stubs).</summary>
    public const string DefaultBaseUrl = "https://gamebanana.com/apiv12";

    /// <summary>GameBanana game id for Mario Kart Wii.</summary>
    public const int MarioKartWiiGameId = 5896;

    const string ModelName = "Mod";
    const string UserAgent = "WheelWizard/2.0";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly HttpClient _http;

    /// <summary>The API base address used for requests, without a trailing slash.</summary>
    public string BaseUrl { get; }

    public GameBananaCatalog(HttpClient http)
        : this(http, null) { }

    public GameBananaCatalog(HttpClient http, string? baseUrl)
    {
        _http = http;
        BaseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        http.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent);
    }

    /// <summary>
    /// Gets a page of mod search results. If you don't provide a search term, the featured mods are
    /// returned (the API uses "Mod" as its own featured list).
    /// </summary>
    public async Task<OperationResult<GameBananaSearchResults>> GetModSearchResults(
        string searchTerm,
        int page = 1,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            searchTerm = "Mod";
        var uri = new Uri(
            $"{BaseUrl}/Util/Search/Results?_sSearchString={Uri.EscapeDataString(searchTerm)}"
                + $"&_idGameRow={MarioKartWiiGameId}&_sModelName={ModelName}&_nPage={page}"
        );
        return await CallAsync<GameBananaSearchResults>(uri, ct);
    }

    /// <summary>Gets all the details of a mod from the GameBanana API.</summary>
    public async Task<OperationResult<GameBananaModDetails>> GetModDetails(int modId, CancellationToken ct = default)
    {
        var uri = new Uri($"{BaseUrl}/Mod/{modId}/ProfilePage");
        return await CallAsync<GameBananaModDetails>(uri, ct);
    }

    async Task<OperationResult<T>> CallAsync<T>(Uri uri, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, Json, ct)
                ?? throw new JsonException($"GameBanana returned an empty payload for {uri}.");
            return OperationResult.Ok(value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new OperationError { Message = $"GameBanana request failed for {uri}: {exception.Message}", Exception = exception };
        }
    }
}
