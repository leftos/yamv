using System.Text;
using Yamv.App.Services;
using Yamv.App.ViewModels;

namespace Yamv.App.Tests.ViewModels;

public sealed class ToastValueTests
{
    private static readonly string Replacement = C(0xFFFD);

    private static readonly string Ellipsis = C(0x2026);

    /// <summary>
    /// Each hostile value and the span it gives: a backtick, a newline, U+202E (right-to-left override), U+200B (zero-width
    /// space), a lone surrogate, U+2028 and U+2029 (line and paragraph separators), and the empty value.
    /// </summary>
    public static TheoryData<string, string> HostileValues =>
        new()
        {
            { "a`b", "`a" + C(0x02CB) + "b`" },
            { "a" + C(0x0A) + "b", "`a" + Replacement + "b`" },
            { "a" + C(0x202E) + "b", "`a" + Replacement + "b`" },
            { "a" + C(0x200B) + "b", "`a" + Replacement + "b`" },
            { "a" + C(0xD800) + "b", "`a" + Replacement + "b`" },
            { "a" + C(0x2028) + "b" + C(0x2029) + "c", "`a" + Replacement + "b" + Replacement + "c`" },
            { "", "`" + Replacement + "`" },
        };

    [Theory]
    [MemberData(nameof(HostileValues))]
    public void Toast_values_cannot_close_the_code_span_break_the_line_or_reorder_text(string value, string expected) =>
        Assert.Equal(expected, ToastValue.Code(value));

    [Theory]
    [MemberData(nameof(HostileValues))]
    public void A_toast_value_is_exactly_one_code_segment(string value, string expected)
    {
        ToastItem item = new(ToastKind.Warning, "x " + ToastValue.Code(value) + " y");

        Assert.Equal([new ToastSegment("x ", false), new ToastSegment(expected[1..^1], true), new ToastSegment(" y", false)], item.Segments);
    }

    [Fact]
    public void A_value_of_64_runes_is_kept_whole()
    {
        string value = new('a', 64);

        Assert.Equal("`" + value + "`", ToastValue.Code(value));
    }

    [Fact]
    public void A_long_value_keeps_its_start_and_end()
    {
        string value = new string('s', 32) + new string('m', 40) + new string('e', 31);

        string code = ToastValue.Code(value);

        Assert.Equal("`" + new string('s', 32) + Ellipsis + new string('e', 31) + "`", code);
        Assert.Equal(64, code[1..^1].EnumerateRunes().Count());
    }

    [Fact]
    public void A_surrogate_pair_is_never_split_by_the_cap()
    {
        string face = char.ConvertFromUtf32(0x1F600);
        string value = string.Concat(Enumerable.Repeat(face, 100));

        string inner = ToastValue.Code(value)[1..^1];

        Assert.Equal(string.Concat(Enumerable.Repeat(face, 32)) + Ellipsis + string.Concat(Enumerable.Repeat(face, 31)), inner);
        Assert.DoesNotContain(inner.EnumerateRunes(), rune => rune == Rune.ReplacementChar);
    }

    [Fact]
    public void Plain_replaces_control_and_format_runes()
    {
        string value = "a" + C(0x0A) + "b" + C(0x202E) + "c" + C(0x2028) + "d";

        Assert.Equal("a" + Replacement + "b" + Replacement + "c" + Replacement + "d", ToastValue.Plain(value));
    }

    [Fact]
    public void Plain_cuts_a_long_value_as_Code_does()
    {
        string value = new string('s', 32) + new string('m', 40) + new string('e', 31);

        Assert.Equal(new string('s', 32) + Ellipsis + new string('e', 31), ToastValue.Plain(value));
        Assert.Equal(ToastValue.Code(value)[1..^1], ToastValue.Plain(value));
    }

    [Fact]
    public void Plain_keeps_a_backtick() => Assert.Equal("a`b", ToastValue.Plain("a`b"));

    private static string C(int code) => ((char)code).ToString();
}
