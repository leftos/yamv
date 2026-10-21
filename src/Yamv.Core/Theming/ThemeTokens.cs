using System.Collections.Frozen;
using System.Text;

namespace Yamv.Core.Theming;

/// <summary>What kind of value a token holds.</summary>
public enum TokenKind
{
    Color,
    Font,
    Number,
    Bool,
}

/// <summary>One theme token: its name in theme files, its group in the editor and its CSS custom property.</summary>
/// <param name="Name">The camelCase name used in theme files.</param>
/// <param name="Kind">The kind of value it holds.</param>
/// <param name="Group">The editor group (Page, Chrome, Code, Syntax, Type, Layout).</param>
/// <param name="CssVariable">The custom property on the document page, <c>--yamv-</c> plus the kebab-case name.</param>
/// <param name="Min">The smallest allowed value of a number token; 0 for the other kinds.</param>
/// <param name="Max">The largest allowed value of a number token; 0 for the other kinds.</param>
/// <param name="Unit">The CSS unit of a number token, <c>px</c>, <c>em</c> or empty.</param>
/// <param name="Label">The friendly name the theme editor shows, such as "Selected row".</param>
public sealed record TokenInfo(string Name, TokenKind Kind, string Group, string CssVariable, double Min, double Max, string Unit, string Label);

/// <summary>The catalogue of theme tokens and their default values.</summary>
public static class ThemeTokens
{
    /// <summary>Every token, in the order of the token table in <c>docs/design/themes.md</c>.</summary>
    public static IReadOnlyList<TokenInfo> All { get; } =
    [
        Color("pageBackground", "Page", "Page background"),
        Color("text", "Page", "Text"),
        Color("textSecondary", "Page", "Secondary text"),
        Color("textMuted", "Page", "Muted text"),
        Color("heading", "Page", "Headings"),
        Color("accent", "Page", "Accent"),
        Color("accentSoft", "Page", "Accent, soft"),
        Color("line", "Page", "Lines"),
        Color("quote", "Page", "Quote bar"),
        Color("chromeBackground", "Chrome", "Title bar and footers"),
        Color("sidebarBackground", "Chrome", "Sidebar"),
        Color("selectionBackground", "Chrome", "Selected row"),
        Color("selectionText", "Chrome", "Selected row text"),
        Color("hoverBackground", "Chrome", "Hovered row"),
        Color("warning", "Chrome", "Warning"),
        Color("warningBackground", "Chrome", "Warning fill"),
        Color("warningLine", "Chrome", "Warning border"),
        Color("codeBackground", "Code", "Code background"),
        Color("codeLine", "Code", "Code block border"),
        Color("findMatch", "Code", "Find match"),
        Color("findCurrent", "Code", "Current match"),
        Color("syntaxKeyword", "Syntax", "Keywords"),
        Color("syntaxString", "Syntax", "Strings"),
        Color("syntaxComment", "Syntax", "Comments"),
        Color("syntaxType", "Syntax", "Types and functions"),
        Color("syntaxNumber", "Syntax", "Numbers"),
        Token("uiFont", TokenKind.Font, "Type", "App font"),
        Token("bodyFont", TokenKind.Font, "Type", "Body font"),
        Token("headingFont", TokenKind.Font, "Type", "Heading font"),
        Token("monoFont", TokenKind.Font, "Type", "Code font"),
        Number("bodySize", "Body size", 10, 32, "px"),
        Number("lineHeight", "Line height", 1.0, 2.5, ""),
        Number("h1Size", "H1 size", 1.0, 4.0, "em"),
        Number("headingWeight", "Heading weight", 100, 900, ""),
        Token("h2Rule", TokenKind.Bool, "Layout", "Rule under H2"),
    ];

    private static readonly Dictionary<string, TokenInfo> ByName = All.ToDictionary(token => token.Name, StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, ThemeValue> LightDefaults = BuildDefaults(
        new Dictionary<string, string>
        {
            ["pageBackground"] = "#ffffff",
            ["text"] = "#1f2328",
            ["textSecondary"] = "#31373d",
            ["textMuted"] = "#656d76",
            ["heading"] = "#111418",
            ["accent"] = "#0969da",
            ["accentSoft"] = "#b6d4f5",
            ["line"] = "#d8dee4",
            ["quote"] = "#d0d7de",
            ["chromeBackground"] = "#f6f8fa",
            ["sidebarBackground"] = "#f6f8fa",
            ["selectionBackground"] = "#e3edfa",
            ["selectionText"] = "#0b3d7a",
            ["hoverBackground"] = "#eaeef2",
            ["warning"] = "#9a6700",
            ["warningBackground"] = "#fff8c5",
            ["warningLine"] = "#e5d17b",
            ["codeBackground"] = "#f6f8fa",
            ["codeLine"] = "#e4e8ec",
            ["findMatch"] = "#fff1a8",
            ["findCurrent"] = "#ffb340",
            ["syntaxKeyword"] = "#cf222e",
            ["syntaxString"] = "#0a3069",
            ["syntaxComment"] = "#6e7781",
            ["syntaxType"] = "#8250df",
            ["syntaxNumber"] = "#0550ae",
        }
    );

    private static readonly FrozenDictionary<string, ThemeValue> DarkDefaults = BuildDefaults(
        new Dictionary<string, string>
        {
            ["pageBackground"] = "#0f1318",
            ["text"] = "#e6edf3",
            ["textSecondary"] = "#c9d1d9",
            ["textMuted"] = "#8b949e",
            ["heading"] = "#f0f6fc",
            ["accent"] = "#4493f8",
            ["accentSoft"] = "#1f3b5e",
            ["line"] = "#2a3139",
            ["quote"] = "#3d444d",
            ["chromeBackground"] = "#0a0d11",
            ["sidebarBackground"] = "#0a0d11",
            ["selectionBackground"] = "#182a40",
            ["selectionText"] = "#e6edf3",
            ["hoverBackground"] = "#161b22",
            ["warning"] = "#d29922",
            ["warningBackground"] = "#bb800926",
            ["warningLine"] = "#9e6a03",
            ["codeBackground"] = "#161b22",
            ["codeLine"] = "#262c34",
            ["findMatch"] = "#5c4813",
            ["findCurrent"] = "#9e6a03",
            ["syntaxKeyword"] = "#ff7b72",
            ["syntaxString"] = "#a5d6ff",
            ["syntaxComment"] = "#8b949e",
            ["syntaxType"] = "#d2a8ff",
            ["syntaxNumber"] = "#79c0ff",
        }
    );

    /// <summary>The token named <paramref name="name"/> (case-sensitive), or null when there is none.</summary>
    public static TokenInfo? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>
    /// The default value of every token: Studio light for <see cref="Appearance.Light"/>, Studio dark for <see cref="Appearance.Dark"/>.
    /// </summary>
    public static FrozenDictionary<string, ThemeValue> DefaultsFor(Appearance appearance) =>
        appearance == Appearance.Dark ? DarkDefaults : LightDefaults;

    private static FrozenDictionary<string, ThemeValue> BuildDefaults(Dictionary<string, string> colors)
    {
        var values = new Dictionary<string, ThemeValue>(StringComparer.Ordinal)
        {
            ["uiFont"] = new FontValue("Inter"),
            ["bodyFont"] = new FontValue("Inter"),
            ["headingFont"] = new FontValue("Inter"),
            ["monoFont"] = new FontValue("JetBrains Mono"),
            ["bodySize"] = new NumberValue(15.5),
            ["lineHeight"] = new NumberValue(1.62),
            ["h1Size"] = new NumberValue(2.05),
            ["headingWeight"] = new NumberValue(700),
            ["h2Rule"] = new BoolValue(true),
        };
        foreach ((string name, string hex) in colors)
        {
            values.Add(name, new ColorValue(hex));
        }

        return values.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static TokenInfo Color(string name, string group, string label) => Token(name, TokenKind.Color, group, label);

    private static TokenInfo Number(string name, string label, double min, double max, string unit) =>
        new(name, TokenKind.Number, "Layout", CssVariableFor(name), min, max, unit, label);

    private static TokenInfo Token(string name, TokenKind kind, string group, string label) =>
        new(name, kind, group, CssVariableFor(name), 0, 0, "", label);

    private static string CssVariableFor(string name)
    {
        var builder = new StringBuilder("--yamv-");
        foreach (char character in name)
        {
            if (char.IsAsciiLetterUpper(character))
            {
                builder.Append('-').Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
