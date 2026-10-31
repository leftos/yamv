using System.Buffers;
using System.Globalization;
using System.Text;

namespace Yamv.Core.Diagnostics;

/// <summary>Puts a value that came from a document, the page or a file name into a log line as text it cannot break out of.</summary>
public static class LogText
{
    /// <summary>
    /// <paramref name="value"/> with every control (Cc), format (Cf), line (Zl) and paragraph (Zp) character and every unpaired
    /// surrogate replaced by U+FFFD, so it cannot break the line or reorder it. Null becomes the empty string, and a value with
    /// nothing to replace is returned as it is. The value is not cut and not quoted.
    /// </summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value) || !NeedsReplacing(value))
        {
            return value ?? string.Empty;
        }

        var text = new StringBuilder(value.Length);
        Span<char> chars = stackalloc char[2];
        foreach (Rune rune in value.EnumerateRunes())
        {
            int written = Safe(rune).EncodeToUtf16(chars);
            text.Append(chars[..written]);
        }

        return text.ToString();
    }

    /// <summary>Whether <paramref name="value"/> holds a rune that could break the line or reorder the text, or no whole surrogate pair at some point.</summary>
    private static bool NeedsReplacing(string value)
    {
        ReadOnlySpan<char> remaining = value;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out Rune rune, out int consumed) != OperationStatus.Done || IsUnsafe(rune))
            {
                return true;
            }

            remaining = remaining[consumed..];
        }

        return false;
    }

    /// <summary><paramref name="rune"/>, or U+FFFD when it could break the line or reorder the text.</summary>
    private static Rune Safe(Rune rune) => IsUnsafe(rune) ? Rune.ReplacementChar : rune;

    private static bool IsUnsafe(Rune rune) =>
        Rune.GetUnicodeCategory(rune)
            is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator;
}
