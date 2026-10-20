using Yamv.Core.Grouping;

namespace Yamv.Core.Documents;

/// <summary>One row of a document window's tree: a group, a folder or a document.</summary>
/// <param name="Name">What the tree row shows for this item.</param>
/// <param name="Path">The full file-system path this item stands for.</param>
public abstract record TreeItem(string Name, string Path);

/// <summary>A top-level group: a git work-tree root, or a folder outside any repository.</summary>
/// <param name="Name">What the tree row shows for the group.</param>
/// <param name="Path">The group root's full path.</param>
/// <param name="IsRepo">True when the group is a git work-tree root, false when it is a plain folder.</param>
/// <param name="IsExpanded">True when the group's children are shown.</param>
/// <param name="Children">The folders and documents directly below the group.</param>
public sealed record GroupItem(string Name, string Path, bool IsRepo, bool IsExpanded, IReadOnlyList<TreeItem> Children) : TreeItem(Name, Path);

/// <summary>A folder below a group.</summary>
/// <param name="Name">What the tree row shows for the folder.</param>
/// <param name="Path">The full path of the folder.</param>
/// <param name="Children">The folders and documents directly below this folder.</param>
public sealed record FolderItem(string Name, string Path, IReadOnlyList<TreeItem> Children) : TreeItem(Name, Path);

/// <summary>One document.</summary>
/// <param name="Name">What the tree row shows for the document.</param>
/// <param name="Path">The document's full path.</param>
/// <param name="GroupPath">The root path of the group the document sits under.</param>
/// <param name="IsMissing">True when the document is no longer on disk.</param>
public sealed record DocumentItem(string Name, string Path, string GroupPath, bool IsMissing) : TreeItem(Name, Path);

/// <summary>The rows a document window's tree shows, and the documents' order in it.</summary>
public sealed record DocumentTree
{
    /// <summary>The tree of a window that holds no documents.</summary>
    public static DocumentTree Empty { get; } = new();

    /// <summary>The top-level rows: one group item per group, in the grouper's order.</summary>
    public IReadOnlyList<TreeItem> Items { get; init; } = [];

    /// <summary>Every document the tree shows, in tree order, each with the group it sits under.</summary>
    public IReadOnlyList<DocumentItem> Documents { get; init; } = [];

    /// <summary>The documents' full paths in tree order, whether or not their group is expanded.</summary>
    public IReadOnlyList<string> DocumentOrder { get; init; } = [];

    /// <summary>
    /// Builds the tree the window shows from the grouper's nodes, marking each document that is no longer on disk
    /// and each group the user collapsed.
    /// </summary>
    /// <param name="groups">The groups the grouper built, already in display order.</param>
    /// <param name="fileExists">Reports whether a document is still on disk.</param>
    /// <param name="collapsedGroups">
    /// The root paths of the groups the user collapsed; a path is matched with the set's own comparer.
    /// </param>
    public static DocumentTree Build(IReadOnlyList<GroupNode> groups, Func<string, bool> fileExists, IReadOnlySet<string> collapsedGroups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(collapsedGroups);

        List<TreeItem> items = [];
        List<DocumentItem> documents = [];
        foreach (GroupNode group in groups)
        {
            List<TreeItem> children = BuildChildren(group.Children, fileExists, group.Path, documents);
            items.Add(new GroupItem(group.Name, group.Path, group.IsRepo, !collapsedGroups.Contains(group.Path), children));
        }

        return new DocumentTree
        {
            Items = items,
            Documents = documents,
            DocumentOrder = [.. documents.Select(document => document.Path)],
        };
    }

    private static List<TreeItem> BuildChildren(
        IReadOnlyList<TreeNode> children,
        Func<string, bool> fileExists,
        string groupPath,
        List<DocumentItem> documents
    )
    {
        List<TreeItem> items = [];
        foreach (TreeNode child in children)
        {
            if (child is DocumentNode document)
            {
                var item = new DocumentItem(document.Name, document.Path, groupPath, !fileExists(document.Path));
                items.Add(item);
                documents.Add(item);
            }
            else if (child is FolderNode folder)
            {
                items.Add(new FolderItem(folder.Name, folder.Path, BuildChildren(folder.Children, fileExists, groupPath, documents)));
            }
            else
            {
                throw new ArgumentException($"A group cannot contain a {child.GetType().Name}.", nameof(children));
            }
        }

        return items;
    }
}
