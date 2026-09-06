using System.Text.Json;
using System.Text.Json.Serialization;

namespace WheelWizard.Core.GameBanana;

public class GameBananaAuthor
{
    [JsonPropertyName("_sName")]
    public required string Name { get; set; }

    [JsonPropertyName("_sProfileUrl")]
    public required string ProfileUrl { get; set; }

    /// <summary>
    /// Some Authors didn't upload an avatar, therefore it can also be null.
    /// </summary>
    [JsonPropertyName("_sAvatarUrl")]
    public string? AvatarUrl { get; set; }
}

public class GameBananaGame
{
    [JsonPropertyName("_sName")]
    public required string Name { get; set; }

    [JsonPropertyName("_sProfileUrl")]
    public required string ProfileUrl { get; set; }

    [JsonPropertyName("_sIconUrl")]
    public required string IconUrl { get; set; }
}

public class GameBananaCategory
{
    /// <summary>
    /// e.g., "Maps", "Characters"
    /// </summary>
    [JsonPropertyName("_sName")]
    public required string Name { get; set; }

    [JsonPropertyName("_sProfileUrl")]
    public required string ProfileUrl { get; set; }

    /// <summary>
    /// Unsure if all categories have an icon, and don't want risk adding `required` here
    /// </summary>
    [JsonPropertyName("_sIconUrl")]
    public string? IconUrl { get; set; }
}

public class GameBananaPreviewMedia
{
    /// <summary>
    /// Not all previews have this
    /// </summary>
    [JsonPropertyName("_aMetadata")]
    public GameBananaPreviewMetaData? MetaData { get; set; }

    [JsonPropertyName("_aImages")]
    public List<GameBananaImage> Images { get; set; } = [];
}

public class GameBananaImage
{
    /// <summary>
    /// media type (e.g., "screenshot")
    /// </summary>
    [JsonPropertyName("_sType")]
    public required string Type { get; set; }

    [JsonPropertyName("_sBaseUrl")]
    public required string BaseUrl { get; set; }

    [JsonPropertyName("_sFile")]
    public required string File { get; set; }

    /// <summary>
    /// Note that the original image can also  be referred to as _sFile100
    /// Sometimes there are higher resolution images available
    /// </summary>
    [JsonPropertyName("_sFile220")]
    public string? File220 { get; set; }

    /// <summary>
    /// Note that the original image can also  be referred to as _sFile100
    /// Sometimes there are higher resolution images available
    /// </summary>
    [JsonPropertyName("_sFile530")]
    public string? File530 { get; set; }
}

public class GameBananaPreviewMetaData
{
    [JsonPropertyName("_Snippet")]
    public string? DescriptionSnippet { get; set; }
}

public class GameBananaLicenseAllowance
{
    // yes it is really true that this class does not prefix it anymore with _x
    // ask GameBanana why

    [JsonPropertyName("yes")]
    public required List<String> Allowed { get; set; }

    [JsonPropertyName("ask")]
    public required List<String> OnRequest { get; set; }

    [JsonPropertyName("no")]
    public required List<String> NotAllowed { get; set; }
}

public class GameBananaTag
{
    public string Title { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class GameBananaTagListJsonConverter : JsonConverter<List<GameBananaTag>>
{
    public override List<GameBananaTag> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return [];

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected StartArray token, but got {reader.TokenType}.");

        var tags = new List<GameBananaTag>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return tags;

            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    var tagText = reader.GetString() ?? string.Empty;
                    tags.Add(new() { Title = tagText, Value = tagText });
                    break;
                case JsonTokenType.StartObject:
                    using (var tagDocument = JsonDocument.ParseValue(ref reader))
                    {
                        var root = tagDocument.RootElement;
                        tags.Add(
                            new()
                            {
                                Title = root.TryGetProperty("_sTitle", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                                Value = root.TryGetProperty("_sValue", out var value) ? value.GetString() ?? string.Empty : string.Empty,
                            }
                        );
                    }
                    break;

                default:
                    using (JsonDocument.ParseValue(ref reader)) { }
                    break;
            }
        }

        throw new JsonException("Unexpected end of GameBanana tag payload.");
    }

    public override void Write(Utf8JsonWriter writer, List<GameBananaTag> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();

        foreach (var tag in value)
        {
            writer.WriteStartObject();
            writer.WriteString("_sTitle", tag.Title);
            writer.WriteString("_sValue", tag.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
