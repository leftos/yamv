using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Yamv.Core.Theming;

/// <summary>Writes a <see cref="ThemeDefinition"/> in the theme file format: indented, LF line endings, tokens in catalogue order.</summary>
public static class ThemeSerializer
{
    public const string SchemaUrl = "https://raw.githubusercontent.com/leftos/yamv/main/themes/yamv-theme.schema.json";

    public const int FormatVersion = 1;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", SchemaUrl);
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("name", theme.Name);
            WriteOptionalString(writer, "author", theme.Author);
            WriteOptionalString(writer, "description", theme.Description);
            WriteTokensAndCss(writer, theme.SharedTokens, theme.SharedCss);
            WriteVariant(writer, "light", theme.Light);
            WriteVariant(writer, "dark", theme.Dark);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteVariant(Utf8JsonWriter writer, string name, ThemeVariant? variant)
    {
        if (variant is null)
        {
            return;
        }

        writer.WriteStartObject(name);
        WriteTokensAndCss(writer, variant.Tokens, variant.Css);
        writer.WriteEndObject();
    }

    private static void WriteTokensAndCss(Utf8JsonWriter writer, IReadOnlyDictionary<string, ThemeValue> tokens, string css)
    {
        if (tokens.Count > 0)
        {
            writer.WriteStartObject("tokens");
            foreach (TokenInfo token in ThemeTokens.All)
            {
                if (tokens.TryGetValue(token.Name, out ThemeValue? value))
                {
                    WriteValue(writer, token.Name, value);
                }
            }

            writer.WriteEndObject();
        }

        if (css.Length > 0)
        {
            writer.WriteString("css", css);
        }
    }

    /// <summary>The JSON text of <paramref name="value"/> as a theme file writes it.</summary>
    /// <exception cref="ArgumentException">The value cannot be written as JSON: a number that is not finite, or invalid text.</exception>
    internal static string ValueText(ThemeValue value) => WriteStandalone(writer => WriteValue(writer, value));

    /// <summary>The JSON string literal of <paramref name="text"/> as a theme file writes it.</summary>
    /// <exception cref="ArgumentException"><paramref name="text"/> is not valid UTF-16.</exception>
    internal static string StringText(string text) => WriteStandalone(writer => writer.WriteStringValue(text));

    private static string WriteStandalone(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, string name, ThemeValue value)
    {
        writer.WritePropertyName(name);
        WriteValue(writer, value);
    }

    private static void WriteValue(Utf8JsonWriter writer, ThemeValue value)
    {
        switch (value)
        {
            case ColorValue color:
                writer.WriteStringValue(color.Hex);
                break;
            case FontValue font:
                writer.WriteStringValue(font.Family);
                break;
            case NumberValue number:
                writer.WriteNumberValue(number.Value);
                break;
            case BoolValue flag:
                writer.WriteBooleanValue(flag.Value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown theme value type.");
        }
    }
}
