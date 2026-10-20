using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Yamv.Core.Theming;

/// <summary>Reads theme file text into a <see cref="ThemeDefinition"/>, following the rules in <c>docs/design/themes.md</c>.</summary>
/// <remarks>
/// Never throws on bad input: anything that makes the theme unusable is an error (and the theme is null); an unknown token or field,
/// or a format version other than <see cref="ThemeSerializer.FormatVersion"/>, is a warning.
/// </remarks>
public static class ThemeParser
{
    private const string SchemaField = "$schema";

    public static ThemeLoadResult Parse(string json, string id, ThemeSource source, string? filePath)
    {
        ArgumentNullException.ThrowIfNull(json);
        ThemeFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize(json, ThemeJsonContext.Default.ThemeFileJson);
        }
        catch (JsonException exception)
        {
            return Invalid($"The file is not valid theme JSON: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return Invalid($"The file is not valid theme JSON: {exception.Message}");
        }

        if (file is null)
        {
            return Invalid("The file is not valid theme JSON: it holds null instead of a theme object.");
        }

        var errors = new List<string>();
        var warnings = new List<string>();
        CheckFile(file, errors, warnings);
        Dictionary<string, ThemeValue> shared = ReadTokens(file.Tokens, "shared", errors, warnings);
        ThemeVariant? light = ReadVariant(file.Light, "light", errors, warnings);
        ThemeVariant? dark = ReadVariant(file.Dark, "dark", errors, warnings);
        if (errors.Count > 0)
        {
            return new ThemeLoadResult(null, errors, warnings);
        }

        var theme = new ThemeDefinition(id, file.Name!, file.Author, file.Description, source, filePath, shared, file.Css ?? "", light, dark);
        return new ThemeLoadResult(theme, errors, warnings);
    }

    private static ThemeLoadResult Invalid(string error) => new(null, [error], []);

    private static void CheckFile(ThemeFileJson file, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(file.Name))
        {
            errors.Add("The theme has no 'name'.");
        }

        if (file.Light is null && file.Dark is null)
        {
            errors.Add("The theme has neither a 'light' nor a 'dark' variant.");
        }

        if (file.FormatVersion is { } version && !IsSupportedVersion(version))
        {
            string shown = EscapeControls(version.GetRawText());
            warnings.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The theme has formatVersion {shown}; this YAMV reads version {ThemeSerializer.FormatVersion} and may miss parts of it."
                )
            );
        }

        foreach (string field in (file.ExtensionData ?? []).Keys.Where(field => field != SchemaField))
        {
            warnings.Add($"Unknown field '{field}' is ignored.");
        }
    }

    private static bool IsSupportedVersion(JsonElement version) =>
        version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int number) && number == ThemeSerializer.FormatVersion;

    private static ThemeVariant? ReadVariant(ThemeVariantJson? variant, string where, List<string> errors, List<string> warnings)
    {
        if (variant is null)
        {
            return null;
        }

        foreach (string field in (variant.ExtensionData ?? []).Keys)
        {
            warnings.Add($"Unknown field '{field}' in {where} is ignored.");
        }

        return new ThemeVariant(ReadTokens(variant.Tokens, where, errors, warnings), variant.Css ?? "");
    }

    private static Dictionary<string, ThemeValue> ReadTokens(
        Dictionary<string, JsonElement>? tokens,
        string where,
        List<string> errors,
        List<string> warnings
    )
    {
        var values = new Dictionary<string, ThemeValue>(StringComparer.Ordinal);
        foreach ((string name, JsonElement element) in tokens ?? [])
        {
            TokenInfo? token = ThemeTokens.Find(name);
            if (token is null)
            {
                warnings.Add($"Unknown token '{name}' in {where} is ignored.");
                continue;
            }

            (ThemeValue? value, string? problem) = ReadValue(token, element);
            if (value is null)
            {
                errors.Add($"Token '{name}' in {where}: {Describe(element)} {problem}");
            }
            else
            {
                values[name] = value;
            }
        }

        return values;
    }

    private static (ThemeValue? Value, string? Problem) ReadValue(TokenInfo token, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && !TryReadString(element, out _))
        {
            return (null, "is not valid text.");
        }

        return token.Kind switch
        {
            TokenKind.Color => ReadColor(element),
            TokenKind.Font => ReadFont(element),
            TokenKind.Number => ReadNumber(token, element),
            TokenKind.Bool => element.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? (new BoolValue(element.GetBoolean()), null)
                : (null, "is not true or false."),
            _ => throw new ArgumentOutOfRangeException(nameof(token), token.Kind, "Unknown token kind."),
        };
    }

    /// <summary>Reads a JSON string, failing on text that is not valid UTF-16 (an unpaired surrogate escape such as <c>\ud800</c>).</summary>
    private static bool TryReadString(JsonElement element, out string? text)
    {
        text = null;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            text = element.GetString();
            return text is not null;
        }
        catch (InvalidOperationException)
        {
            // The caller reports the value as "is not valid text"; the exception carries nothing more for the theme author.
            return false;
        }
    }

    private static (ThemeValue? Value, string? Problem) ReadColor(JsonElement element)
    {
        string? hex = TryReadString(element, out string? text) ? ColorValue.Normalize(text!) : null;
        return hex is null ? (null, "is not a colour (#rgb, #rrggbb or #rrggbbaa).") : (new ColorValue(hex), null);
    }

    private static (ThemeValue? Value, string? Problem) ReadFont(JsonElement element)
    {
        TryReadString(element, out string? family);
        return string.IsNullOrWhiteSpace(family) || family.Any(char.IsControl) ? (null, "is not a font family name.") : (new FontValue(family), null);
    }

    private static (ThemeValue? Value, string? Problem) ReadNumber(TokenInfo token, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out double number))
        {
            return (null, "is not a number.");
        }

        return number < token.Min || number > token.Max ? (null, $"is out of range ({RangeText(token)}).") : (new NumberValue(number), null);
    }

    /// <summary>
    /// The token's range as <c>10–32</c>, or with one decimal (<c>1.0–2.5</c>, <c>1.0–4.0</c>) for a token whose values are fractional:
    /// a bound has a fraction, or the whole range lies below 10.
    /// </summary>
    private static string RangeText(TokenInfo token)
    {
        bool fractional = token.Min % 1 != 0 || token.Max % 1 != 0 || token.Max < 10;
        string format = fractional ? "0.0" : "0";
        return token.Min.ToString(format, CultureInfo.InvariantCulture) + "–" + token.Max.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string Describe(JsonElement element) =>
        EscapeControls(TryReadString(element, out string? text) ? $"'{text}'" : element.GetRawText());

    /// <summary>Writes every control character in <paramref name="text"/> as <c>\uXXXX</c>, so file content cannot forge a log line.</summary>
    private static string EscapeControls(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            if (char.IsControl(character))
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:X4}");
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
