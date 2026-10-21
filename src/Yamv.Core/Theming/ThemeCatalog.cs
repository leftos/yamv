using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Yamv.Core.Diagnostics;

namespace Yamv.Core.Theming;

/// <summary>The built-in themes embedded in the app and the user themes in <paramref name="userFolder"/>.</summary>
/// <remarks>
/// <para>
/// The catalog reads the folder when asked and has no watcher. The app's watcher on the user folder should: debounce its events,
/// then re-run <see cref="LoadAll"/>; treat a Renamed event as a change; ignore <c>*.tmp</c> files, which are writes in progress;
/// and ignore the transient Deleted event a replace produces (every write here goes to a temporary file that is then moved over
/// the theme file), since the file is back by the time the debounced <see cref="LoadAll"/> runs.
/// </para>
/// <para>
/// User files are read with sharing that allows a concurrent write or delete. Windows still refuses to replace a file while any
/// handle has it open, so a write that finds the file in use retries the replace 5 times, 50 ms apart, and then fails with the
/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> of the last attempt.
/// </para>
/// <para>
/// Each write goes to <c>.&lt;32 hex digits&gt;.tmp</c> beside its target. <see cref="LoadAll"/> deletes such files in the user
/// folder once they are more than a minute old, which a write cut short by a crash leaves behind; the age spares a write in progress
/// in another YAMV instance sharing the folder.
/// </para>
/// </remarks>
public sealed partial class ThemeCatalog(string userFolder, IAppLog log)
{
    public const string FileSuffix = ".yamv-theme.json";

    /// <summary>The most bytes a theme file is read for; a theme file is a few KB.</summary>
    internal const int MaxFileBytes = 1024 * 1024;

    /// <summary><see cref="MaxFileBytes"/> written out, as the messages about a file over it report it.</summary>
    internal static readonly string MaxFileSizeText = MaxFileBytes / (1024 * 1024) + " MB";

    /// <summary>The built-in theme the app uses when no other theme is chosen or the chosen one is gone: Studio.</summary>
    public const string DefaultThemeId = "studio";

    private const string ResourcePrefix = "Yamv.Core.Theming.BuiltIn.";
    private const int MoveAttempts = 5;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromMinutes(1);

    /// <summary>The built-in theme ids, in the order they are listed.</summary>
    public static IReadOnlyList<string> BuiltInIds { get; } = ["paper", "studio", "ink"];

    public string UserFolder { get; } = userFolder;

    /// <summary>
    /// The built-in themes, then the user themes sorted by name. A valid user theme whose id matches a built-in one replaces it;
    /// an invalid one is listed beside the built-in. Invalid themes are listed with their errors. Ids can repeat: an invalid user
    /// file that shadows a built-in is listed beside the built-in. Look a theme up by id preferring the valid entry.
    /// </summary>
    /// <remarks>
    /// First deletes the stale temporary files of interrupted writes from the user folder. A user file whose name gives an id
    /// <see cref="ThemeIds.IsValid"/> rejects, a Windows device name (<see cref="ThemeIds.IsDeviceName"/>) included, is listed as
    /// invalid on every platform without being read, since <see cref="Save"/>, <see cref="Rename"/> and <see cref="Delete"/> refuse
    /// its id. A file larger than <see cref="MaxFileBytes"/> is listed as invalid with a read error, and one that goes away between
    /// the listing and the read is skipped.
    /// </remarks>
    public IReadOnlyList<ThemeEntry> LoadAll()
    {
        SweepStaleTemporaries();
        List<ThemeEntry> user = LoadUserThemes();
        var shadowing = new HashSet<string>(user.Where(entry => entry.Result.Theme is not null).Select(entry => entry.Id), ThemeIds.Comparer);
        var entries = new List<ThemeEntry>();
        foreach (string id in BuiltInIds.Where(id => !shadowing.Contains(id)))
        {
            ThemeLoadResult result = ThemeParser.Parse(ReadBuiltInText(id), id, ThemeSource.BuiltIn, filePath: null);
            Report(id, "built-in", result);
            entries.Add(new ThemeEntry(id, ThemeSource.BuiltIn, null, result));
        }

        entries.AddRange(
            user.OrderBy(entry => entry.Result.Theme?.Name ?? entry.Id, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
        );
        return entries;
    }

    /// <summary>
    /// Copies the theme file at <paramref name="sourcePath"/> into the user folder, verbatim, under its own id or, when that is a
    /// user or built-in theme already, the first free <c>-2</c>, <c>-3</c>… variant of it. The source is read with sharing that
    /// allows a concurrent write, so a file an editor holds open imports as it stands.
    /// </summary>
    /// <exception cref="ThemeException">
    /// The file is not a valid theme, its name gives no usable id, or it is larger than <see cref="MaxFileBytes"/>; nothing is
    /// written.
    /// </exception>
    public ThemeEntry Import(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        string text = ReadImportedText(sourcePath);
        string baseId = ThemeIds.FromFileName(sourcePath);
        if (!ThemeIds.IsValid(baseId))
        {
            string error = NoUsableIdError(sourcePath);
            throw new ThemeException(error, [error]);
        }

        ThemeLoadResult check = ThemeParser.Parse(text, baseId, ThemeSource.User, sourcePath);
        if (check.Theme is null)
        {
            throw new ThemeException($"{sourcePath} is not a valid theme: {string.Join(" ", check.Errors)}", check.Errors);
        }

        string id = CreateNew(baseId, _ => text, []);
        string path = PathFor(id);
        log.LogInfo($"Imported the theme {sourcePath} as {path}.");
        return new ThemeEntry(id, ThemeSource.User, path, ThemeParser.Parse(text, id, ThemeSource.User, path));
    }

    /// <summary>
    /// Reads the theme file at <paramref name="path"/> for the Import notice, listing the errors <see cref="Import"/> would fail with,
    /// without copying it. A file that cannot be read, or is larger than <see cref="MaxFileBytes"/>, gives the read error.
    /// </summary>
    public static ThemeInspection Inspect(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string fileName = Path.GetFileName(path);
        string text;
        try
        {
            text = ReadShared(path).Text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ThemeInspection(fileName, null, null, false, false, false, [$"The file cannot be read: {exception.Message}"], []);
        }

        string id = ThemeIds.FromFileName(path);
        ThemeLoadResult result = ThemeParser.Parse(text, id, ThemeSource.User, path);
        List<string> errors = ThemeIds.IsValid(id) ? [] : [NoUsableIdError(path)];
        errors.AddRange(result.Errors);
        if (result.Theme is not { } theme)
        {
            return new ThemeInspection(fileName, null, null, false, false, false, errors, result.Warnings);
        }

        bool hasCss = new[] { theme.SharedCss, theme.Light?.Css, theme.Dark?.Css }.Any(css => !string.IsNullOrWhiteSpace(css));
        return new ThemeInspection(
            fileName,
            theme.Name,
            theme.Author,
            theme.Light is not null,
            theme.Dark is not null,
            hasCss,
            errors,
            result.Warnings
        );
    }

    /// <summary>
    /// Writes <paramref name="theme"/> to <paramref name="destinationPath"/>: a built-in theme as its embedded file, comments
    /// included, and a user theme as <see cref="ThemeSerializer"/> writes it.
    /// </summary>
    public void Export(ThemeDefinition theme, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(destinationPath);
        string text = theme.Source == ThemeSource.BuiltIn ? ReadBuiltInText(theme.Id) : ThemeSerializer.Serialize(theme);
        Replace(destinationPath, text);
        log.LogInfo($"Exported the theme '{theme.Id}' to {destinationPath}.");
    }

    /// <summary>
    /// Writes an editable copy of <paramref name="theme"/> into the user folder under the first free <c>-2</c>, <c>-3</c>… variant
    /// of its id, named "<c>&lt;Name&gt; (copy)</c>" for <c>-2</c>, "<c>&lt;Name&gt; (copy 2)</c>" for <c>-3</c>, and so on.
    /// </summary>
    /// <exception cref="ThemeException">The copy would not be a valid theme; nothing is written.</exception>
    public ThemeDefinition Duplicate(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ThemeDefinition CopyAs(string id) => theme with { Id = id, Name = CopyName(theme, id), Source = ThemeSource.User, FilePath = PathFor(id) };

        string id = CreateNew(theme.Id, candidate => SerializeValid(CopyAs(candidate)), [theme.Id]);
        ThemeDefinition copy = CopyAs(id);
        log.LogInfo($"Duplicated the theme '{theme.Id}' as {copy.FilePath}.");
        return copy;
    }

    /// <summary>
    /// Writes a user theme to <c>&lt;id&gt;.yamv-theme.json</c> in the user folder, leaving no partial file behind. When the file
    /// exists, only what changed is rewritten inside its text, so its comments, field order and unknown fields stay; otherwise the
    /// theme is written whole.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="theme"/> is built in.</exception>
    /// <exception cref="ThemeException">
    /// <paramref name="theme"/> is not a valid theme, or the existing file cannot be updated in place; nothing is written.
    /// </exception>
    public void Save(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (theme.Source == ThemeSource.BuiltIn)
        {
            throw new InvalidOperationException($"The built-in theme '{theme.Id}' is read-only; duplicate it to edit it.");
        }

        string path = PathFor(theme.Id);
        string text = SerializeValid(theme);
        if (File.Exists(path))
        {
            (string current, bool bom) = ReadShared(path);
            text = WithBom(PatchedText(current, path, ParseOnDisk(current, theme.Id, path), theme), bom);
        }

        Replace(path, text);
        log.LogInfo($"Saved the theme {path}.");
    }

    /// <summary>
    /// Renames the user theme <paramref name="id"/> to <paramref name="newName"/>, patching the name inside its file so it keeps its
    /// comments, field order and unknown fields, then moves the file to the id <see cref="ThemeIds.Slug"/> gives the name, or the
    /// first free <c>-2</c>, <c>-3</c>… variant of it. A theme already filed under that slug (<c>mine</c> or <c>mine-3</c> for
    /// "Mine") keeps its file. When the move fails, the theme stays renamed under its old id and a warning is logged. Returns the
    /// renamed theme.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="id"/> is a built-in theme with no user file shadowing it.</exception>
    /// <exception cref="FileNotFoundException">There is no user theme <paramref name="id"/>.</exception>
    /// <exception cref="ThemeException">The name is blank, or the file is not a valid theme or cannot be updated in place.</exception>
    public ThemeDefinition Rename(string id, string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);
        string path = ExistingUserFile(id, "renamed");
        (string current, bool bom) = ReadShared(path);
        ThemeDefinition onDisk = ParseOnDisk(current, id, path);
        ThemeDefinition renamed = onDisk with { Name = newName };
        SerializeValid(renamed);
        string text = PatchedText(current, path, onDisk, renamed);
        if (text != current)
        {
            Replace(path, WithBom(text, bom));
        }

        string slug = ThemeIds.Slug(newName);
        if (IsFiledUnder(id, slug))
        {
            log.LogInfo($"Renamed the theme {path} to '{newName}'.");
            return renamed;
        }

        return MoveRenamed(renamed, path, slug);
    }

    /// <summary>Moves the file of the user theme <paramref name="id"/> to the Recycle Bin or Trash through <paramref name="trash"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="id"/> is a built-in theme with no user file shadowing it.</exception>
    /// <exception cref="FileNotFoundException">There is no user theme <paramref name="id"/>.</exception>
    public void Delete(string id, IFileTrash trash)
    {
        ArgumentNullException.ThrowIfNull(trash);
        string path = ExistingUserFile(id, "deleted");
        trash.MoveToTrash(path);
        log.LogInfo($"Moved the theme {path} to the trash.");
    }

    /// <summary>The path of the user file of theme <paramref name="id"/>, which must exist.</summary>
    private string ExistingUserFile(string id, string action)
    {
        string path = PathFor(id);
        if (File.Exists(path))
        {
            return path;
        }

        if (BuiltInIds.Contains(id, ThemeIds.Comparer))
        {
            throw new InvalidOperationException($"The built-in theme '{id}' cannot be {action}.");
        }

        throw new FileNotFoundException($"There is no user theme '{id}' to be {action}.", path);
    }

    /// <summary>Whether <paramref name="id"/> is <paramref name="slug"/> or a numbered variant of it, <c>&lt;slug&gt;-&lt;n&gt;</c>.</summary>
    private static bool IsFiledUnder(string id, string slug)
    {
        if (ThemeIds.Comparer.Equals(id, slug))
        {
            return true;
        }

        return id.Length > slug.Length + 1
            && id.StartsWith(slug + "-", StringComparison.OrdinalIgnoreCase)
            && id.AsSpan(slug.Length + 1).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>Moves the renamed theme's file at <paramref name="path"/> to the first free id from <paramref name="slug"/>.</summary>
    private ThemeDefinition MoveRenamed(ThemeDefinition renamed, string path, string slug)
    {
        HashSet<string> taken = TakenIds();
        if (ThemeIds.IsDeviceName(slug))
        {
            taken.Add(slug);
        }

        while (true)
        {
            string newId = ThemeIds.Unique(slug, taken);
            string destination = PathFor(newId);
            try
            {
                if (MoveWithRetry(path, destination, overwrite: false))
                {
                    log.LogInfo($"Renamed the theme {path} to '{renamed.Name}', now {destination}.");
                    return renamed with { Id = newId, FilePath = destination };
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log.LogWarning($"Renamed the theme {path} to '{renamed.Name}' but cannot move it to {destination}; it keeps its file.", exception);
                return renamed;
            }

            taken.Add(newId);
        }
    }

    private static ThemeDefinition ParseOnDisk(string text, string id, string path) =>
        ThemeParser.Parse(text, id, ThemeSource.User, path).Theme
        ?? throw ThemeFilePatch.CannotUpdate(path, "the file on disk is not a valid theme.");

    /// <summary><paramref name="current"/> edited to hold <paramref name="wanted"/>, checked by <see cref="CheckPatch"/>.</summary>
    private static string PatchedText(string current, string path, ThemeDefinition onDisk, ThemeDefinition wanted) =>
        CheckPatch(current, ThemeFilePatch.Apply(current, path, onDisk, wanted), path, wanted);

    /// <summary>
    /// <paramref name="patched"/>, once it reads back as <paramref name="wanted"/> with the same warnings <paramref name="current"/>
    /// gives, so its unknown fields, <c>$schema</c> and <c>formatVersion</c> came through intact.
    /// </summary>
    /// <exception cref="ThemeException">It does not; the file is left as it is.</exception>
    internal static string CheckPatch(string current, string patched, string path, ThemeDefinition wanted)
    {
        ThemeLoadResult before = ThemeParser.Parse(current, wanted.Id, wanted.Source, path);
        ThemeLoadResult after = ThemeParser.Parse(patched, wanted.Id, wanted.Source, path);
        bool intact =
            after.Theme is not null
            && after.Theme.ContentEquals(wanted)
            && after.Warnings.Order(StringComparer.Ordinal).SequenceEqual(before.Warnings.Order(StringComparer.Ordinal));
        return intact ? patched : throw ThemeFilePatch.CannotUpdate(path, "the edited text would not read back as the theme being saved.");
    }

    private static string WithBom(string text, bool bom) => bom ? "\uFEFF" + text : text;

    /// <summary>
    /// The text of the user file at <paramref name="path"/>, read with sharing that allows a concurrent write or delete, without its
    /// UTF-8 byte order mark, and whether it had one.
    /// </summary>
    /// <exception cref="ThemeFileTooLargeException">The file is larger than <see cref="MaxFileBytes"/>.</exception>
    private static (string Text, bool Bom) ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string text = Encoding.UTF8.GetString(ReadAtMost(stream));
        return text.StartsWith('\uFEFF') ? (text[1..], true) : (text, false);
    }

    /// <summary>The bytes of <paramref name="stream"/>, read no further than one byte past <see cref="MaxFileBytes"/>.</summary>
    /// <exception cref="ThemeFileTooLargeException">There is that one byte, so the file is larger than the cap.</exception>
    internal static byte[] ReadAtMost(Stream stream)
    {
        using var bytes = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                int read = stream.Read(chunk, 0, Math.Min(chunk.Length, MaxFileBytes + 1 - (int)bytes.Length));
                if (read == 0)
                {
                    return bytes.ToArray();
                }

                bytes.Write(chunk, 0, read);
                if (bytes.Length > MaxFileBytes)
                {
                    throw new ThemeFileTooLargeException();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>The text of the file at <paramref name="sourcePath"/> being imported.</summary>
    /// <exception cref="ThemeException">The file is larger than <see cref="MaxFileBytes"/>.</exception>
    private static string ReadImportedText(string sourcePath)
    {
        try
        {
            return ReadShared(sourcePath).Text;
        }
        catch (ThemeFileTooLargeException exception)
        {
            string error = $"{sourcePath} is larger than {MaxFileSizeText}; a theme file is a few KB.";
            throw new ThemeException(error, [error], exception);
        }
    }

    /// <summary>The text of the embedded built-in theme <paramref name="id"/>.</summary>
    internal static string ReadBuiltInText(string id)
    {
        string resource = ResourceName(id);
        using Stream stream =
            typeof(ThemeCatalog).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The built-in theme resource {resource} is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string NoUsableIdError(string path) =>
        $"The file name '{Path.GetFileName(path)}' gives no usable theme id; rename it to <name>{FileSuffix}.";

    /// <summary>The manifest resource name the built-in theme <paramref name="id"/> is embedded under.</summary>
    internal static string ResourceName(string id) => ResourcePrefix + id + FileSuffix;

    /// <summary>The file text of <paramref name="theme"/>, checked to read back as a valid theme.</summary>
    /// <exception cref="ThemeException"><paramref name="theme"/> cannot be written, or would not read back as a valid theme.</exception>
    private static string SerializeValid(ThemeDefinition theme)
    {
        List<string> unwritable = UnwritableParts(theme);
        if (unwritable.Count > 0)
        {
            throw new ThemeException($"The theme '{theme.Id}' cannot be written: {string.Join(" ", unwritable)}", unwritable);
        }

        string text;
        try
        {
            text = ThemeSerializer.Serialize(theme);
        }
        catch (ArgumentException exception)
        {
            string error = $"The theme cannot be written as JSON: {exception.Message}";
            throw new ThemeException($"The theme '{theme.Id}' cannot be written: {error}", [error], exception);
        }

        ThemeLoadResult check = ThemeParser.Parse(text, theme.Id, theme.Source, theme.FilePath);
        if (check.Theme is null)
        {
            throw new ThemeException($"The theme '{theme.Id}' is not valid: {string.Join(" ", check.Errors)}", check.Errors);
        }

        return text;
    }

    /// <summary>The parts of <paramref name="theme"/> JSON cannot hold: a number that is not finite, or text with an unpaired surrogate.</summary>
    private static List<string> UnwritableParts(ThemeDefinition theme)
    {
        var errors = new List<string>();
        AddIfNotText(errors, theme.Name, "The theme's 'name' is not valid text.");
        AddIfNotText(errors, theme.Author, "The theme's 'author' is not valid text.");
        AddIfNotText(errors, theme.Description, "The theme's 'description' is not valid text.");
        AddUnwritableTokensAndCss(errors, theme.SharedTokens, theme.SharedCss, "shared");
        if (theme.Light is { } light)
        {
            AddUnwritableTokensAndCss(errors, light.Tokens, light.Css, "light");
        }

        if (theme.Dark is { } dark)
        {
            AddUnwritableTokensAndCss(errors, dark.Tokens, dark.Css, "dark");
        }

        return errors;
    }

    private static void AddUnwritableTokensAndCss(List<string> errors, IReadOnlyDictionary<string, ThemeValue> tokens, string css, string where)
    {
        foreach ((string name, ThemeValue value) in tokens)
        {
            string? problem = value switch
            {
                NumberValue number when !double.IsFinite(number.Value) => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{number.Value} is not a finite number."
                ),
                FontValue font when !IsValidText(font.Family) => "is not valid text.",
                _ => null,
            };
            if (problem is not null)
            {
                errors.Add($"Token '{name}' in {where}: {problem}");
            }
        }

        AddIfNotText(errors, css, $"The css in {where} is not valid text.");
    }

    private static void AddIfNotText(List<string> errors, string? text, string error)
    {
        if (text is not null && !IsValidText(text))
        {
            errors.Add(error);
        }
    }

    /// <summary>Whether <paramref name="text"/> is valid UTF-16: every surrogate is half of a pair.</summary>
    private static bool IsValidText(string text)
    {
        ReadOnlySpan<char> rest = text;
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out _, out int used) != OperationStatus.Done)
            {
                return false;
            }

            rest = rest[used..];
        }

        return true;
    }

    private string PathFor(string id)
    {
        ThemeIds.EnsureValid(id);
        return Path.Combine(UserFolder, id + FileSuffix);
    }

    private IEnumerable<string> UserFiles() =>
        Directory.EnumerateFiles(UserFolder, "*" + FileSuffix).Where(path => path.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase));

    private List<ThemeEntry> LoadUserThemes()
    {
        var entries = new List<ThemeEntry>();
        if (!Directory.Exists(UserFolder))
        {
            return entries;
        }

        List<string> paths;
        try
        {
            paths = [.. UserFiles()];
        }
        catch (IOException exception)
        {
            log.LogError($"Cannot list the themes folder {UserFolder}; only the built-in themes are listed.", exception);
            return entries;
        }
        catch (UnauthorizedAccessException exception)
        {
            log.LogError($"Cannot list the themes folder {UserFolder}; only the built-in themes are listed.", exception);
            return entries;
        }

        foreach (string path in paths)
        {
            string id = ThemeIds.FromFileName(path);
            ThemeLoadResult? result = LoadUserFile(id, path);
            if (result is null)
            {
                continue;
            }

            Report(id, path, result);
            entries.Add(new ThemeEntry(id, ThemeSource.User, path, result));
        }

        return entries;
    }

    /// <summary>The outcome of reading the user file at <paramref name="path"/>, or null when it went away before it could be read.</summary>
    private ThemeLoadResult? LoadUserFile(string id, string path)
    {
        if (ThemeIds.IsDeviceName(id))
        {
            return new ThemeLoadResult(null, [$"\"{Path.GetFileName(path)}\" uses a name Windows reserves for a device; rename the file."], []);
        }

        return ThemeIds.IsValid(id) ? ReadUserTheme(id, path) : new ThemeLoadResult(null, [NoUsableIdError(path)], []);
    }

    /// <summary>Deletes the temporary files in the user folder that are older than <see cref="StaleTemporaryAge"/>.</summary>
    private void SweepStaleTemporaries()
    {
        if (!Directory.Exists(UserFolder))
        {
            return;
        }

        DateTime cutoff = DateTime.UtcNow - StaleTemporaryAge;
        List<string> stale;
        try
        {
            stale =
            [
                .. Directory
                    .EnumerateFiles(UserFolder, "*.tmp")
                    .Where(path => TemporaryName().IsMatch(Path.GetFileName(path)) && File.GetLastWriteTimeUtc(path) < cutoff),
            ];
        }
        catch (IOException exception)
        {
            log.LogWarning($"Cannot look for stale temporary files in {UserFolder}; leaving them.", exception);
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            log.LogWarning($"Cannot look for stale temporary files in {UserFolder}; leaving them.", exception);
            return;
        }

        foreach (string path in stale)
        {
            DeleteStaleTemporary(path);
        }
    }

    private void DeleteStaleTemporary(string path)
    {
        try
        {
            File.Delete(path);
            log.LogInfo($"Deleted the stale temporary file {path}, left by a write that did not finish.");
        }
        catch (IOException exception)
        {
            log.LogWarning($"Cannot delete the stale temporary file {path}; leaving it.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            log.LogWarning($"Cannot delete the stale temporary file {path}; leaving it.", exception);
        }
    }

    /// <summary>The parsed user file at <paramref name="path"/>, or null when it went away before it could be read.</summary>
    private ThemeLoadResult? ReadUserTheme(string id, string path)
    {
        try
        {
            return ThemeParser.Parse(ReadShared(path).Text, id, ThemeSource.User, path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            log.LogInfo($"The theme file {path} went away while it was being read; skipping it.");
            return null;
        }
        catch (ThemeFileTooLargeException exception)
        {
            log.LogWarning($"Cannot read the theme file {path}: it is larger than {MaxFileSizeText}.", exception);
            return new ThemeLoadResult(null, [$"The file cannot be read: {exception.Message}"], []);
        }
        catch (IOException exception)
        {
            log.LogError($"Cannot read the theme file {path}.", exception);
            return new ThemeLoadResult(null, [$"The file cannot be read: {exception.Message}"], []);
        }
        catch (UnauthorizedAccessException exception)
        {
            log.LogError($"Cannot read the theme file {path}.", exception);
            return new ThemeLoadResult(null, [$"The file cannot be read: {exception.Message}"], []);
        }
    }

    private void Report(string id, string where, ThemeLoadResult result)
    {
        foreach (string warning in result.Warnings)
        {
            log.LogWarning($"Theme '{id}' ({where}): {warning}");
        }

        if (result.Theme is null)
        {
            log.LogWarning($"Theme '{id}' ({where}) is invalid: {string.Join(" ", result.Errors)}");
        }
    }

    /// <summary>The name of the copy of <paramref name="theme"/> written as <paramref name="copyId"/>, <c>&lt;id&gt;-&lt;n&gt;</c>.</summary>
    private static string CopyName(ThemeDefinition theme, string copyId)
    {
        int number = int.Parse(copyId.AsSpan(theme.Id.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture);
        return number == 2 ? theme.Name + " (copy)" : string.Create(CultureInfo.InvariantCulture, $"{theme.Name} (copy {number - 1})");
    }

    /// <summary>
    /// Writes a new user theme file under <paramref name="baseId"/> or the first free numbered variant of it, never replacing a file:
    /// when another writer creates the chosen file first, the next number is tried. Ids in <paramref name="alsoTaken"/> are never
    /// chosen. <paramref name="textFor"/> gives the file text for the id being tried. Returns the id written.
    /// </summary>
    private string CreateNew(string baseId, Func<string, string> textFor, IEnumerable<string> alsoTaken)
    {
        HashSet<string> taken = TakenIds();
        taken.UnionWith(alsoTaken);
        while (true)
        {
            string id = ThemeIds.Unique(baseId, taken);
            string path = PathFor(id);
            string temporary = WriteTemporary(path, textFor(id));
            try
            {
                if (MoveWithRetry(temporary, path, overwrite: false))
                {
                    return id;
                }
            }
            finally
            {
                DeleteIfPresent(temporary);
            }

            taken.Add(id);
        }
    }

    /// <summary>The built-in ids and the ids of every user file.</summary>
    private HashSet<string> TakenIds()
    {
        var taken = new HashSet<string>(BuiltInIds, ThemeIds.Comparer);
        if (Directory.Exists(UserFolder))
        {
            taken.UnionWith(UserFiles().Select(ThemeIds.FromFileName));
        }

        return taken;
    }

    /// <summary>Replaces <paramref name="path"/> with <paramref name="text"/>, leaving no partial file behind.</summary>
    private void Replace(string path, string text)
    {
        string temporary = WriteTemporary(path, text);
        try
        {
            MoveWithRetry(temporary, path, overwrite: true);
        }
        finally
        {
            DeleteIfPresent(temporary);
        }
    }

    private static string WriteTemporary(string path, string text)
    {
        string directory = Path.GetDirectoryName(path) ?? "";
        if (directory.Length > 0)
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, text);
        return temporary;
    }

    /// <summary>
    /// Moves <paramref name="temporary"/> to <paramref name="path"/>, retrying a locked destination a few times. Returns false when
    /// <paramref name="overwrite"/> is false and <paramref name="path"/> already exists.
    /// </summary>
    private bool MoveWithRetry(string temporary, string path, bool overwrite)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite);
                return true;
            }
            catch (IOException exception) when (!overwrite && File.Exists(path))
            {
                log.LogWarning($"{path} was created by someone else while YAMV was writing it; trying the next free id.", exception);
                return false;
            }
            catch (IOException exception) when (attempt < MoveAttempts)
            {
                WaitToRetry(path, attempt, exception);
            }
            catch (UnauthorizedAccessException exception) when (attempt < MoveAttempts)
            {
                WaitToRetry(path, attempt, exception);
            }
        }
    }

    private void WaitToRetry(string path, int attempt, Exception exception)
    {
        log.LogWarning($"Cannot move the new version into {path} (attempt {attempt} of {MoveAttempts}); retrying.", exception);
        Thread.Sleep(MoveRetryDelay);
    }

    private static void DeleteIfPresent(string temporary)
    {
        if (File.Exists(temporary))
        {
            File.Delete(temporary);
        }
    }

    [GeneratedRegex(@"^\.[0-9a-f]{32}\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex TemporaryName();
}
