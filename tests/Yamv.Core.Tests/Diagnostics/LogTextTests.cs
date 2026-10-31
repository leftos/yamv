using Yamv.Core.Diagnostics;

namespace Yamv.Core.Tests.Diagnostics;

public sealed class LogTextTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\t")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u200E")]
    [InlineData("\u202E")]
    [InlineData("\uFEFF")]
    public void A_character_that_could_break_or_reorder_a_line_becomes_the_replacement_character(string breaking) =>
        Assert.Equal("a\uFFFDb", LogText.Clean("a" + breaking + "b"));

    [Fact]
    public void An_unpaired_surrogate_becomes_the_replacement_character()
    {
        Assert.Equal("a\uFFFDb", LogText.Clean("a\uD800b"));
        Assert.Equal("a\uFFFDb", LogText.Clean("a\uDC00b"));
        Assert.Equal("a\uFFFD", LogText.Clean("a\uD800"));
        Assert.Equal("\uFFFD\uFFFD", LogText.Clean("\uDC00\uD800"));
    }

    [Theory]
    [InlineData("plain text with spaces")]
    [InlineData("Ünïcödé — 日本語")]
    [InlineData("😀")]
    public void Text_that_cannot_break_a_line_passes_unchanged(string value) => Assert.Equal(value, LogText.Clean(value));

    [Fact]
    public void Null_becomes_the_empty_string() => Assert.Equal(string.Empty, LogText.Clean(null));
}
