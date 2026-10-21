using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yamv.Core.Diagnostics;

namespace Yamv.Core.Widths;

/// <summary>
/// Reads and writes the widths file's JSON: <c>{ "default": 800 | "full", "folders": { "&lt;folder&gt;": 760 | "full" } }</c>.
/// </summary>
/// <remarks>
/// A bad entry (a value that is not a px number above zero or <c>"full"</c>, or a key that is not a full folder path)
/// is dropped alone with a warning; a bad default is 800 px; a px value below 480 is raised to 480. Folder keys that
/// name the same folder are merged: the later value wins and the first spelling is kept.
/// </remarks>
internal static class WidthsFile
{
    private const string FullValue = "full";

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Parses <paramref name="bytes"/>, logging a warning for the file or each entry it cannot use.</summary>
    /// <returns><see langword="false"/> when the bytes are not a JSON object.</returns>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string filePath, IAppLog log, [NotNullWhen(true)] out WidthsState? state)
    {
        if (bytes.StartsWith(Utf8Bom))
        {
            bytes = bytes[Utf8Bom.Length..];
        }

        WidthsFileJson json;
        try
        {
            json =
                JsonSerializer.Deserialize(bytes, WidthsJsonContext.Default.WidthsFileJson)
                ?? throw new JsonException("The widths file holds JSON null instead of an object.");
        }
        catch (JsonException exception)
        {
            log.LogWarning($"The widths file {filePath} is unreadable; keeping the widths read before it.", exception);
            state = null;
            return false;
        }

        state = new WidthsState(DecodeDefault(json.Default, filePath, log), DecodeFolders(json.Folders, filePath, log), json.ExtensionData);
        return true;
    }

    /// <summary>The file's bytes for <paramref name="state"/>: px as JSON numbers, Full as <c>"full"</c>, folders sorted.</summary>
    public static byte[] Serialize(WidthsState state)
    {
        var folders = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (FolderWidth folder in state.Folders.Values)
        {
            folders[folder.Folder] = Encode(folder.Width);
        }

        var json = new WidthsFileJson
        {
            Default = Encode(state.Default),
            Folders = JsonSerializer.SerializeToElement(folders, WidthsJsonContext.Default.SortedDictionaryStringJsonElement),
            ExtensionData = state.UnknownFields is { } unknown ? new Dictionary<string, JsonElement>(unknown) : null,
        };
        return JsonSerializer.SerializeToUtf8Bytes(json, WidthsJsonContext.Default.WidthsFileJson);
    }

    private static ContentWidth DecodeDefault(JsonElement? element, string filePath, IAppLog log)
    {
        if (element is not { } value)
        {
            return ContentWidth.AppDefault;
        }

        if (Decode(value) is { } width)
        {
            return width;
        }

        log.LogWarning($"The widths file {filePath} has a default of {value.GetRawText()}, which is not a px number or \"full\"; using 800 px.");
        return ContentWidth.AppDefault;
    }

    private static Dictionary<string, FolderWidth> DecodeFolders(JsonElement? element, string filePath, IAppLog log)
    {
        var folders = new Dictionary<string, FolderWidth>(AppPaths.PathComparer);
        if (element is not { } value || value.ValueKind == JsonValueKind.Null)
        {
            return folders;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            log.LogWarning($"The widths file {filePath} has \"folders\": {value.GetRawText()}, which is not an object; ignoring it.");
            return folders;
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            string? key = WidthKeys.TryNormalize(property.Name);
            if (key is null || Decode(property.Value) is not { } width)
            {
                log.LogWarning(
                    $"The widths file {filePath} has the entry \"{property.Name}\": {property.Value.GetRawText()}, which is not a full folder "
                        + "path with a px number or \"full\"; dropping it."
                );
                continue;
            }

            string spelling = folders.TryGetValue(key, out FolderWidth existing) ? existing.Folder : key;
            folders[key] = new FolderWidth(spelling, width);
        }

        return folders;
    }

    private static ContentWidth? Decode(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out double px) && double.IsFinite(px) && px > 0 ? ContentWidth.FromPx(px) : null,
            JsonValueKind.String => string.Equals(value.GetString(), FullValue, StringComparison.OrdinalIgnoreCase) ? ContentWidth.Full : null,
            _ => null,
        };

    private static JsonElement Encode(ContentWidth width) =>
        width.Px is { } px
            ? JsonSerializer.SerializeToElement(px, WidthsJsonContext.Default.Double)
            : JsonSerializer.SerializeToElement(FullValue, WidthsJsonContext.Default.String);
}

/// <summary>The on-disk shape of the widths file. Values stay raw so one bad entry is dropped alone.</summary>
internal sealed class WidthsFileJson
{
    public JsonElement? Default { get; set; }

    public JsonElement? Folders { get; set; }

    /// <summary>Every field the format does not define, kept through a rewrite.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Source-generated serializer metadata, so the widths file works under AOT and trimming.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(WidthsFileJson))]
[JsonSerializable(typeof(SortedDictionary<string, JsonElement>))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(string))]
internal sealed partial class WidthsJsonContext : JsonSerializerContext { }
