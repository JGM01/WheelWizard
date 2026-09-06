using System.Text.Json.Serialization;

namespace WheelWizard.Core.GameBanana;

/// <summary>
/// A mod as it appears in a GameBanana search result list.
/// </summary>
public class GameBananaModPreview
{
    // Properties in common with GameBananaModDetails

    [JsonPropertyName("_idRow")]
    public required int Id { get; set; }

    [JsonPropertyName("_sName")]
    public required string Name { get; set; }

    [JsonPropertyName("_sVersion")]
    public required string Version { get; set; }

    [JsonPropertyName("_aTags")]
    [JsonConverter(typeof(GameBananaTagListJsonConverter))]
    public required List<GameBananaTag> Tags { get; set; }

    [JsonPropertyName("_sProfileUrl")]
    public required string ProfileUrl { get; set; }

    [JsonPropertyName("_aPreviewMedia")]
    public required GameBananaPreviewMedia PreviewMedia { get; set; }

    [JsonPropertyName("_bHasContentRatings")]
    public required bool HasContentRatings { get; set; }

    [JsonPropertyName("_nLikeCount")]
    public int LikeCount { get; set; }

    [JsonPropertyName("_nViewCount")]
    public int ViewCount { get; set; }

    [JsonPropertyName("_tsDateAdded")]
    public required long DateAdded { get; set; }

    [JsonPropertyName("_tsDateModified")]
    public required long DateModified { get; set; }

    [JsonPropertyName("_aSubmitter")]
    public required GameBananaAuthor Author { get; set; }

    [JsonPropertyName("_aGame")]
    public required GameBananaGame Game { get; set; }

    // Unique properties to the Mod Details

    [JsonPropertyName("_aRootCategory")]
    public required GameBananaCategory RootCategory { get; set; }

    /// <summary>
    /// e.g. "Mod" , "Question", "Thread", "Sound"
    /// </summary>
    [JsonPropertyName("_sModelName")]
    public required string ModelName { get; set; }

    [JsonIgnore]
    public bool UsesPatches => Tags.Any(tag => IsPatchesTag(tag.Title));

    private static bool IsPatchesTag(string? tagTitle)
    {
        var normalizedTitle = tagTitle?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return false;

        var titleOnly = normalizedTitle.Split(':', 2)[0].Trim();
        return titleOnly.Equals("patch", StringComparison.OrdinalIgnoreCase)
            || titleOnly.Equals("patches", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The full details of a single GameBanana mod.
/// </summary>
public class GameBananaModDetails
{
    // Properties in common with GameBananaModPreview

    [JsonPropertyName("_idRow")]
    public required int Id { get; set; }

    [JsonPropertyName("_sName")]
    public required string Name { get; set; }

    [JsonPropertyName("_sVersion")]
    public required string Version { get; set; }

    [JsonPropertyName("_sProfileUrl")]
    public required string ProfileUrl { get; set; }

    [JsonPropertyName("_aPreviewMedia")]
    public GameBananaPreviewMedia? PreviewMedia { get; set; }

    [JsonPropertyName("_nLikeCount")]
    public required int LikeCount { get; set; }

    [JsonPropertyName("_nViewCount")]
    public required int ViewCount { get; set; }

    [JsonPropertyName("_tsDateAdded")]
    public required long DateAdded { get; set; }

    [JsonPropertyName("_tsDateModified")]
    public required long DateModified { get; set; }

    [JsonPropertyName("_bIsObsolete")]
    public required bool IsObsolete { get; set; }

    [JsonPropertyName("_aSubmitter")]
    public required GameBananaAuthor Author { get; set; }

    [JsonPropertyName("_aGame")]
    public required GameBananaGame Game { get; set; }

    // Unique properties to the Mod Details

    [JsonPropertyName("_aCategory")]
    public required GameBananaCategory Category { get; set; }

    [JsonPropertyName("_aSuperCategory")]
    public GameBananaCategory? SuperCategory { get; set; }

    [JsonPropertyName("_sText")]
    public required string Text { get; set; }

    [JsonPropertyName("_sLicense")]
    public required string License { get; set; }

    [JsonPropertyName("_aLicenseCheckList")]
    public GameBananaLicenseAllowance? LicenseAllowance { get; set; }

    [JsonPropertyName("_nDownloadCount")]
    public required int DownloadCount { get; set; }

    [JsonPropertyName("_aFiles")]
    public List<GameBananaModFiles>? Files { get; set; }

    [JsonPropertyName("_aArchivedFiles")]
    public List<GameBananaModFiles>? ArchivedFiles { get; set; }
}

/// <summary>
/// A downloadable file attached to a GameBanana mod.
/// </summary>
public class GameBananaModFiles
{
    [JsonPropertyName("_sFile")]
    public required string FileName { get; set; }

    [JsonPropertyName("_nFilesize")]
    public int FileSize { get; set; }

    [JsonPropertyName("_sDownloadUrl")]
    public required string DownloadUrl { get; set; }
}

/// <summary>
/// A page of GameBanana search results.
/// </summary>
public class GameBananaSearchResults
{
    /// <summary>
    /// Metadata for the API response (e.g., total records, pagination)
    /// </summary>
    [JsonPropertyName("_aMetadata")]
    public required GameBananaSearchMetaData MetaData { get; set; }

    /// <summary>
    ///  List of records representing mods or other GameBanana content
    /// </summary>
    [JsonPropertyName("_aRecords")]
    public required List<GameBananaModPreview> Records { get; set; }
}

/// <summary>
/// Pagination metadata returned alongside GameBanana search results.
/// </summary>
public class GameBananaSearchMetaData
{
    /// <summary>
    /// Number of records found in total given the search parameters
    /// </summary>
    [JsonPropertyName("_nRecordCount")]
    public required int RecordCount { get; set; }

    /// <summary>
    /// Number of records per page returned by the API
    /// </summary>
    [JsonPropertyName("_nPerpage")]
    public required int PerPage { get; set; }

    [JsonPropertyName("_bIsComplete")]
    public required bool IsComplete { get; set; }
}
