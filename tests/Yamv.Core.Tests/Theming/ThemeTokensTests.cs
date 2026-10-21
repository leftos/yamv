using Yamv.Core.Theming;

namespace Yamv.Core.Tests.Theming;

public sealed class ThemeTokensTests
{
    [Fact]
    public void All_EveryTokenHasALabel_AndLabelsAreUnique()
    {
        Assert.All(ThemeTokens.All, token => Assert.False(string.IsNullOrWhiteSpace(token.Label), token.Name));
        Assert.Equal(ThemeTokens.All.Count, ThemeTokens.All.Select(token => token.Label).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("selectionBackground", "Selected row")]
    [InlineData("chromeBackground", "Title bar and footers")]
    [InlineData("h2Rule", "Rule under H2")]
    public void Find_GivesTheFriendlyLabel(string name, string label) => Assert.Equal(label, ThemeTokens.Find(name)!.Label);
}
