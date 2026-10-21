using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yamv.Core.Diagnostics;

namespace Yamv.Core.Settings;

/// <summary>
/// Reads and writes the settings file's JSON: <c>{ "themeId": "&lt;theme id&gt;", "appearance": "system" }</c>.
/// </summary>
/// <remarks>
/// A value that is not a non-empty string, or an appearance that is not <c>system</c>, <c>light</c> or <c>dark</c>, is
/// replaced by its default with a warning, and the other value still loads. A theme id is never checked against the theme
/// catalog: whether the theme still exists is the app's business.
/// </remarks>
internal static class SettingsFile
{
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Parses <paramref name="bytes"/>, logging a warning for the file or each value it cannot use.</summary>
    /// <returns><see langword="false"/> when the bytes are not a JSON object.</returns>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string filePath, IAppLog log, [NotNullWhen(true)] out SettingsState? state)
    {
        if (bytes.StartsWith(Utf8Bom))
        {
            bytes = bytes[Utf8Bom.Length..];
        }

        SettingsFileJson json;
        try
        {
            json =
                JsonSerializer.Deserialize(bytes, SettingsJsonContext.Default.SettingsFileJson)
                ?? throw new JsonException("The settings file holds JSON null instead of an object.");
        }
        catch (JsonException exception)
        {
            log.LogWarning($"The settings file {filePath} is unreadable; keeping the settings read before it.", exception);
            state = null;
            return false;
        }

        var values = new AppSettings(DecodeThemeId(json.ThemeId, filePath, log), DecodeAppearance(json.Appearance, filePath, log));
        state = new SettingsState(values, json.ExtensionData);
        return true;
    }

    /// <summary>The file's bytes for <paramref name="state"/>: <c>themeId</c> then <c>appearance</c>, then any unknown fields.</summary>
    public static byte[] Serialize(SettingsState state)
    {
        var json = new SettingsFileJson
        {
            ThemeId = JsonSerializer.SerializeToElement(state.Values.ThemeId, SettingsJsonContext.Default.String),
            Appearance = JsonSerializer.SerializeToElement(state.Values.Appearance, SettingsJsonContext.Default.String),
            ExtensionData = state.UnknownFields is { } unknown ? new Dictionary<string, JsonElement>(unknown) : null,
        };
        return JsonSerializer.SerializeToUtf8Bytes(json, SettingsJsonContext.Default.SettingsFileJson);
    }

    private static string DecodeThemeId(JsonElement value, string filePath, IAppLog log)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return AppSettings.Default.ThemeId;
        }

        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        log.LogWarning(
            $"The settings file {filePath} has a themeId of {value.GetRawText()}, which is not a theme id; "
                + $"using \"{AppSettings.Default.ThemeId}\"."
        );
        return AppSettings.Default.ThemeId;
    }

    private static string DecodeAppearance(JsonElement value, string filePath, IAppLog log)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return AppSettings.SystemAppearance;
        }

        string? appearance = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (AppSettings.IsAppearance(appearance))
        {
            return appearance;
        }

        log.LogWarning(
            $"The settings file {filePath} has an appearance of {value.GetRawText()}, which is not "
                + $"\"{AppSettings.SystemAppearance}\", \"{AppSettings.LightAppearance}\" or \"{AppSettings.DarkAppearance}\"; "
                + $"using \"{AppSettings.SystemAppearance}\"."
        );
        return AppSettings.SystemAppearance;
    }
}

/// <summary>The on-disk shape of the settings file. Values stay raw so one bad value is replaced alone.</summary>
internal sealed class SettingsFileJson
{
    /// <summary>
    /// Absent from the file as <see cref="JsonValueKind.Undefined"/>, so a written <c>null</c> is a bad value rather than
    /// a missing one.
    /// </summary>
    public JsonElement ThemeId { get; set; }

    /// <inheritdoc cref="ThemeId"/>
    public JsonElement Appearance { get; set; }

    /// <summary>Every field the format does not define, kept through a rewrite.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Source-generated serializer metadata, so the settings file works under AOT and trimming.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(SettingsFileJson))]
[JsonSerializable(typeof(string))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext { }
