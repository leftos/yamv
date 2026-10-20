namespace Yamv.Core.Grouping;

/// <summary>One entry in the document tree: a group, a folder or a document.</summary>
/// <param name="Name">What the tree shows for this entry.</param>
/// <param name="Path">The full file-system path this entry stands for.</param>
public abstract record TreeNode(string Name, string Path);

/// <summary>A top-level group of documents: a git work-tree root, or a folder outside any repository.</summary>
/// <param name="Name">What the tree shows for the group.</param>
/// <param name="Path">The group root's full path.</param>
/// <param name="IsRepo">True when the group is a git work-tree root, false when it is a plain folder.</param>
/// <param name="Children">The folders and documents directly below the group.</param>
public sealed record GroupNode(string Name, string Path, bool IsRepo, IReadOnlyList<TreeNode> Children) : TreeNode(Name, Path);

/// <summary>A folder below a group. A chain of single-child folders is shown as one node, named <c>a/b</c>.</summary>
/// <param name="Name">What the tree shows for the folder.</param>
/// <param name="Path">The full path of the deepest folder the node stands for.</param>
/// <param name="Children">The folders and documents directly below this folder.</param>
public sealed record FolderNode(string Name, string Path, IReadOnlyList<TreeNode> Children) : TreeNode(Name, Path);

/// <summary>A single document.</summary>
public sealed record DocumentNode(string Name, string Path) : TreeNode(Name, Path);
