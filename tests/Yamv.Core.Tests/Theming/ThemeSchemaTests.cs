using System.Globalization;
using System.Text.Json;
using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class ThemeSchemaTests
{
    [Fact]
    public void Schema_TokenProperties_AreExactlyTheCatalogTokens()
    {
        string schemaPath = Path.Combine(RepoRoot(), "themes", "yamv-theme.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));

        JsonElement properties = schema.RootElement.GetProperty("$defs").GetProperty("tokens").GetProperty("properties");

        Assert.Equal(ThemeTokens.All.Select(token => token.Name), properties.EnumerateObject().Select(property => property.Name));
        foreach (TokenInfo token in ThemeTokens.All)
        {
            Assert.Equal(ExpectedShape(token), ShapeOf(properties.GetProperty(token.Name)));
        }
    }

    private static string ExpectedShape(TokenInfo token) =>
        token.Kind switch
        {
            TokenKind.Color => "ref #/$defs/color",
            TokenKind.Font => "ref #/$defs/font",
            TokenKind.Number => $"number {token.Min.ToString(CultureInfo.InvariantCulture)}..{token.Max.ToString(CultureInfo.InvariantCulture)}",
            TokenKind.Bool => "boolean",
            _ => throw new ArgumentOutOfRangeException(nameof(token), token.Kind, "Unknown token kind."),
        };

    private static string ShapeOf(JsonElement property)
    {
        if (property.TryGetProperty("$ref", out JsonElement reference))
        {
            return "ref " + reference.GetString();
        }

        string type = property.GetProperty("type").GetString()!;
        if (type != "number")
        {
            return type;
        }

        string minimum = property.GetProperty("minimum").GetDouble().ToString(CultureInfo.InvariantCulture);
        string maximum = property.GetProperty("maximum").GetDouble().ToString(CultureInfo.InvariantCulture);
        return $"number {minimum}..{maximum}";
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Yamv.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No folder above {AppContext.BaseDirectory} holds Yamv.slnx.");
    }
}
