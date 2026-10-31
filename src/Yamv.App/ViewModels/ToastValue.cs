using System.Globalization;
using System.Text;

namespace Yamv.App.ViewModels;

/// <summary>Puts a value from a link or a path into a toast's text as inline code it cannot break out of.</summary>
public static class ToastValue
{
    /// <summary>The most runes a value shows, the <c>…</c> of a cut value included.</summary>
    private const int MaxRunes = 64;

    /// <summary>The runes kept from the start of a cut value; the rest of <see cref="MaxRunes"/> after the <c>…</c> come from its end.</summary>
    private const int HeadRunes = 32;

    /// <summary>U+02CB, the modifier letter grave accent a backtick shows as.</summary>
    private static readonly Rune BacktickStandIn = new(0x02CB);

    /// <summary>U+2026, the horizontal ellipsis between a cut value's start and end.</summary>
    private static readonly Rune Ellipsis = new(0x2026);

    /// <summary>
    /// The whole inline-code span for <paramref name="value"/>, backticks included. A backtick becomes U+02CB, so the value
    /// cannot close the span; every control, format (such as U+202E or U+200B), line or paragraph separator rune and every
    /// unpaired surrogate becomes U+FFFD, so it cannot break the line or reorder the text. A value of more than 64 runes keeps
    /// its first 32 and last 31 with <c>…</c> between them, never splitting a surrogate pair. An empty value shows as U+FFFD.
    /// </summary>
    public static string Code(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string backtickStandIn = BacktickStandIn.ToString();
        return "`" + Plain(value).Replace("`", backtickStandIn, StringComparison.Ordinal) + "`";
    }

    /// <summary>
    /// <paramref name="value"/> as plain text it cannot break out of: sanitised and cut as <see cref="Code"/> does, with its
    /// backticks kept and none added.
    /// </summary>
    public static string Plain(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        List<Rune> runes = [.. value.EnumerateRunes().Select(Safe)];
        if (runes.Count == 0)
        {
            runes.Add(Rune.ReplacementChar);
        }
        else if (runes.Count > MaxRunes)
        {
            runes = [.. runes.Take(HeadRunes), Ellipsis, .. runes.TakeLast(MaxRunes - HeadRunes - 1)];
        }

        var text = new StringBuilder();
        foreach (Rune rune in runes)
        {
            text.Append(rune.ToString());
        }

        return text.ToString();
    }

    /// <summary>
    /// <paramref name="rune"/>, or U+FFFD when it could break the line or reorder the text. <see cref="string.EnumerateRunes"/>
    /// already gives U+FFFD for an unpaired surrogate.
    /// </summary>
    private static Rune Safe(Rune rune) =>
        Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator =>
                Rune.ReplacementChar,
            _ => rune,
        };
}
