using System.Text.Json;

namespace Yamv.Core.Widths;

/// <summary>One folder's own width, with the folder spelled as it was first stored.</summary>
internal readonly record struct FolderWidth(string Folder, ContentWidth Width);

/// <summary>
/// What the widths file holds, read-only. Folders are keyed with <see cref="AppPaths.PathComparer"/>; each change
/// returns a new state, or <see langword="null"/> when it would change nothing.
/// </summary>
internal sealed class WidthsState(
    ContentWidth defaultWidth,
    IReadOnlyDictionary<string, FolderWidth> folders,
    IReadOnlyDictionary<string, JsonElement>? unknownFields
)
{
    public static WidthsState Empty { get; } = new(ContentWidth.AppDefault, new Dictionary<string, FolderWidth>(AppPaths.PathComparer), null);

    public ContentWidth Default { get; } = defaultWidth;

    public IReadOnlyDictionary<string, FolderWidth> Folders { get; } = folders;

    /// <summary>Top-level fields the format does not define, kept so a rewrite does not lose them.</summary>
    public IReadOnlyDictionary<string, JsonElement>? UnknownFields { get; } = unknownFields;

    public WidthsState? WithDefault(ContentWidth width) => width == Default ? null : new WidthsState(width, Folders, UnknownFields);

    /// <summary>Sets <paramref name="key"/>'s width, keeping the spelling of a folder already stored.</summary>
    public WidthsState? WithFolder(string key, ContentWidth width)
    {
        bool stored = Folders.TryGetValue(key, out FolderWidth existing);
        if (stored && existing.Width == width)
        {
            return null;
        }

        var folders = new Dictionary<string, FolderWidth>(Folders, AppPaths.PathComparer)
        {
            [key] = new FolderWidth(stored ? existing.Folder : key, width),
        };
        return new WidthsState(Default, folders, UnknownFields);
    }

    /// <summary>Removes every folder <paramref name="matches"/> accepts.</summary>
    public WidthsState? Without(Func<string, bool> matches)
    {
        var folders = new Dictionary<string, FolderWidth>(AppPaths.PathComparer);
        foreach ((string key, FolderWidth folder) in Folders)
        {
            if (!matches(key))
            {
                folders[key] = folder;
            }
        }

        return folders.Count == Folders.Count ? null : new WidthsState(Default, folders, UnknownFields);
    }
}
