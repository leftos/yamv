namespace Yamv.Core.Grouping;

/// <summary>Builds the document tree: one group per git work tree or loose folder, with folders and documents below it.</summary>
public static class DocumentGrouper
{
    /// <summary>
    /// Groups <paramref name="filePaths"/> into the tree the main window shows. A file lands under the work-tree root
    /// <paramref name="findRepoRoot"/> reports for it, or under its own folder when that returns null.
    /// </summary>
    /// <param name="filePaths">Document paths; normalized and de-duplicated here.</param>
    /// <param name="findRepoRoot">Reports the git work-tree root that contains a file, or null when it is in none.</param>
    /// <param name="options">The home folder and path comparison that names and de-duplication use.</param>
    public static IReadOnlyList<GroupNode> Group(IEnumerable<string> filePaths, Func<string, string?> findRepoRoot, GroupingOptions options)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(findRepoRoot);
        ArgumentNullException.ThrowIfNull(options);

        var pathComparer = StringComparer.FromComparison(options.PathComparison);
        List<GroupBuilder> groups = Collect(pathComparer, filePaths, findRepoRoot);
        if (groups.Count == 0)
        {
            return [];
        }

        AssignNames(groups, options);
        return
        [
            .. groups
                .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => new GroupNode(group.Name, group.RootPath, group.IsRepo, group.Root.BuildChildren())),
        ];
    }

    private static List<GroupBuilder> Collect(StringComparer pathComparer, IEnumerable<string> filePaths, Func<string, string?> findRepoRoot)
    {
        Dictionary<string, GroupBuilder> groups = new(pathComparer);
        List<GroupBuilder> ordered = [];
        HashSet<string> seen = new(pathComparer);
        foreach (string rawPath in filePaths)
        {
            string filePath = Path.GetFullPath(rawPath);
            if (!seen.Add(filePath))
            {
                continue;
            }

            string? repoRoot = findRepoRoot(filePath);
            string folder = Path.GetDirectoryName(filePath) ?? filePath;
            string rootPath = Path.TrimEndingDirectorySeparator(repoRoot ?? folder);
            if (!groups.TryGetValue(rootPath, out GroupBuilder? group))
            {
                group = new GroupBuilder(rootPath, repoRoot is not null, pathComparer);
                groups.Add(rootPath, group);
                ordered.Add(group);
            }

            group.Add(filePath);
        }

        return ordered;
    }

    private static void AssignNames(List<GroupBuilder> groups, GroupingOptions options)
    {
        StringComparer nameComparer = StringComparer.OrdinalIgnoreCase;
        var names = groups.ToDictionary(group => group, group => BaseName(group, options));
        foreach (
            IGrouping<string, KeyValuePair<GroupBuilder, string>>? cluster in names
                .GroupBy(pair => pair.Value, nameComparer)
                .Where(cluster => cluster.Count() > 1)
                .ToList()
        )
        {
            Dictionary<GroupBuilder, string> candidates = [];
            foreach (KeyValuePair<GroupBuilder, string> pair in cluster)
            {
                candidates[pair.Key] = $"{pair.Value} ({ParentFolderName(pair.Key.RootPath)})";
            }

            List<KeyValuePair<GroupBuilder, string>> candidateList = [.. candidates];
            foreach (
                IGrouping<string, KeyValuePair<GroupBuilder, string>>? clash in candidateList
                    .GroupBy(pair => pair.Value, nameComparer)
                    .Where(clash => clash.Count() > 1)
                    .ToList()
            )
            {
                foreach (KeyValuePair<GroupBuilder, string> pair in clash)
                {
                    candidates[pair.Key] = DisplayPath(pair.Key.RootPath, options);
                }
            }

            foreach (KeyValuePair<GroupBuilder, string> pair in candidates)
            {
                names[pair.Key] = pair.Value;
            }
        }

        foreach (GroupBuilder group in groups)
        {
            group.Name = names[group];
        }
    }

    private static string BaseName(GroupBuilder group, GroupingOptions options) =>
        group.IsRepo ? FolderName(group.RootPath) : DisplayPath(group.RootPath, options);

    private static string ParentFolderName(string path)
    {
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        return parent is null ? FolderName(path) : FolderName(parent);
    }

    private static string FolderName(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        string name = Path.GetFileName(trimmed);
        return name.Length == 0 ? trimmed : name;
    }

    private static string DisplayPath(string path, GroupingOptions options)
    {
        string? home = options.HomeDirectory;
        if (string.IsNullOrEmpty(home))
        {
            return path;
        }

        string trimmedHome = Path.TrimEndingDirectorySeparator(home);
        if (trimmedHome.Equals(Path.GetPathRoot(trimmedHome), options.PathComparison))
        {
            return path;
        }

        if (path.Equals(trimmedHome, options.PathComparison))
        {
            return "~";
        }

        string prefix = Path.EndsInDirectorySeparator(trimmedHome) ? trimmedHome : trimmedHome + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, options.PathComparison) ? "~" + path[trimmedHome.Length..] : path;
    }

    private static FolderNode BuildFolderNode(FolderBuilder folder)
    {
        List<FolderNode> folders = [.. folder.Folders.Select(BuildFolderNode).OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)];
        List<DocumentNode> documents = [.. folder.Documents.OrderBy(document => document.Name, StringComparer.OrdinalIgnoreCase)];
        string name = FolderName(folder.FullPath);
        string path = folder.FullPath;
        while (folders.Count == 1 && documents.Count == 0)
        {
            FolderNode only = folders[0];
            name = $"{name}/{only.Name}";
            path = only.Path;
            folders = [.. only.Children.OfType<FolderNode>()];
            documents = [.. only.Children.OfType<DocumentNode>()];
        }

        return new FolderNode(name, path, [.. folders, .. documents]);
    }

    private sealed class GroupBuilder(string rootPath, bool isRepo, StringComparer comparer)
    {
        public string RootPath { get; } = rootPath;

        public bool IsRepo { get; } = isRepo;

        public FolderBuilder Root { get; } = new(rootPath, comparer);

        public string Name { get; set; } = string.Empty;

        public void Add(string filePath)
        {
            string relative = Path.GetRelativePath(RootPath, filePath);
            string[] parts =
            [
                .. relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries),
            ];
            if (parts.Length == 0 || relative == ".")
            {
                Root.Documents.Add(new DocumentNode(Path.GetFileName(filePath), filePath));
                return;
            }

            FolderBuilder folder = Root;
            for (int index = 0; index < parts.Length - 1; index++)
            {
                folder = folder.GetOrAddFolder(parts[index]);
            }

            folder.Documents.Add(new DocumentNode(parts[^1], filePath));
        }
    }

    private sealed class FolderBuilder(string fullPath, StringComparer comparer)
    {
        private readonly Dictionary<string, FolderBuilder> folders = new(comparer);

        public string FullPath { get; } = fullPath;

        public List<DocumentNode> Documents { get; } = [];

        public IEnumerable<FolderBuilder> Folders => folders.Values;

        public FolderBuilder GetOrAddFolder(string name)
        {
            string path = Path.Combine(FullPath, name);
            if (!folders.TryGetValue(path, out FolderBuilder? folder))
            {
                folder = new FolderBuilder(path, comparer);
                folders.Add(path, folder);
            }

            return folder;
        }

        public List<TreeNode> BuildChildren()
        {
            List<FolderNode> folderNodes = [.. Folders.Select(BuildFolderNode).OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)];
            List<DocumentNode> documentNodes = [.. Documents.OrderBy(document => document.Name, StringComparer.OrdinalIgnoreCase)];
            return [.. folderNodes, .. documentNodes];
        }
    }
}
