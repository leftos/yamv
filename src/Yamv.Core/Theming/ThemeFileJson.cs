using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yamv.Core.Theming;

/// <summary>The on-disk shape of a theme file. Token values stay raw so the parser can report each one by name.</summary>
internal sealed class ThemeFileJson
{
    /// <summary>Kept raw so a value that is not an integer is a warning rather than a file that will not load.</summary>
    public JsonElement? FormatVersion { get; set; }

    public string? Name { get; set; }

    public string? Author { get; set; }

    public string? Description { get; set; }

    public Dictionary<string, JsonElement>? Tokens { get; set; }

    public string? Css { get; set; }

    public ThemeVariantJson? Light { get; set; }

    public ThemeVariantJson? Dark { get; set; }

    /// <summary>Every field the format does not define (<c>$schema</c> included), so the parser can warn about them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>The on-disk shape of a theme file's <c>light</c> or <c>dark</c> object.</summary>
internal sealed class ThemeVariantJson
{
    public Dictionary<string, JsonElement>? Tokens { get; set; }

    public string? Css { get; set; }

    /// <summary>Every field the format does not define, so the parser can warn about them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Source-generated serializer metadata for theme files, so they load under AOT and trimming.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    RespectNullableAnnotations = true
)]
[JsonSerializable(typeof(ThemeFileJson))]
internal sealed partial class ThemeJsonContext : JsonSerializerContext { }
