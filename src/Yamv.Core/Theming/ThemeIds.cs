using System.Globalization;
using System.Text.RegularExpressions;

namespace Yamv.Core.Theming;

/// <summary>Theme ids: a theme's id is its file name without <see cref="ThemeCatalog.FileSuffix"/>.</summary>
public static partial class ThemeIds
{
    /// <summary>Ids compare case-insensitively, so two files that differ only in case never both exist.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// The id of the theme in <paramref name="fileName"/>: the name without <see cref="ThemeCatalog.FileSuffix"/>, exactly as written,
    /// or without its extension when it lacks the suffix.
    /// </summary>
    public static string FromFileName(string fileName)
    {
        string name = Path.GetFileName(fileName);
        return name.EndsWith(ThemeCatalog.FileSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^ThemeCatalog.FileSuffix.Length]
            : Path.GetFileNameWithoutExtension(name);
    }

    /// <summary><paramref name="baseId"/> when it is free, else the first free of <c>-2</c>, <c>-3</c>… appended to it.</summary>
    public static string Unique(string baseId, IReadOnlySet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        if (!taken.Contains(baseId))
        {
            return baseId;
        }

        for (int suffix = 2; ; suffix++)
        {
            string candidate = string.Create(CultureInfo.InvariantCulture, $"{baseId}-{suffix}");
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Whether <paramref name="id"/> can name a file inside the themes folder.</summary>
    /// <remarks>
    /// <para>
    /// On every platform it rejects an empty id, <c>.</c> and <c>..</c>; an id holding <c>/</c> or <c>\</c>; an id holding a
    /// control character (<see cref="char.IsControl(char)"/>: U+0000–U+001F and U+007F–U+009F); and a Windows device name
    /// (<see cref="IsDeviceName"/>).
    /// </para>
    /// <para>
    /// It also rejects the characters <see cref="Path.GetInvalidFileNameChars"/> returns on the running platform: on Windows
    /// <c>"</c>, <c>&lt;</c>, <c>&gt;</c>, <c>|</c>, <c>:</c>, <c>*</c> and <c>?</c> besides the ones above; on macOS none beyond them.
    /// </para>
    /// </remarks>
    public static bool IsValid(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.Length > 0
            && id is not ("." or "..")
            && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !id.Contains('/', StringComparison.Ordinal)
            && !id.Contains('\\', StringComparison.Ordinal)
            && !id.Any(char.IsControl)
            && !IsDeviceName(id);
    }

    /// <summary>
    /// Whether <paramref name="id"/> is a name Windows reserves for a device, on any platform: <c>con</c>, <c>prn</c>, <c>aux</c>,
    /// <c>nul</c>, <c>com0</c>–<c>com9</c>, <c>lpt0</c>–<c>lpt9</c> or <c>com</c>/<c>lpt</c> with <c>¹</c>, <c>²</c> or <c>³</c>,
    /// in any case, alone or followed by an extension (<c>com1.txt</c>).
    /// </summary>
    public static bool IsDeviceName(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return DeviceName().IsMatch(id);
    }

    /// <summary>Throws when <paramref name="id"/> is not <see cref="IsValid"/>.</summary>
    public static void EnsureValid(string id)
    {
        if (!IsValid(id))
        {
            throw new ArgumentException($"'{id}' is not a valid theme id: it must be a plain file name.", nameof(id));
        }
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();
}
