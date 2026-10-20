using System.Collections.Concurrent;
using Yamv.Core.Diagnostics;

namespace Yamv.Core.Grouping;

/// <summary>
/// Finds the git work-tree root that contains a file, by walking up from the file's folder. The cache never sees a later
/// <c>git init</c> or a deleted <c>.git</c>, so a folder the finder has already answered for keeps its cached answer.
/// </summary>
/// <param name="log">Where a folder that cannot be probed is reported.</param>
public sealed class GitRootFinder(IAppLog log)
{
    private readonly ConcurrentDictionary<string, string?> knownRoots = new(AppPaths.PathComparer);

    /// <summary>
    /// Returns the git work-tree root that contains <paramref name="filePath"/>, or null when the file is in no repository.
    /// The innermost repository wins, so a submodule is its own root. Answers are cached per folder visited.
    /// </summary>
    public string? FindWorkTreeRoot(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (directory is null)
        {
            return null;
        }

        string? root = null;
        bool probeFailed = false;
        List<string> visited = [];
        for (string? current = directory; current is not null; current = Directory.GetParent(current)?.FullName)
        {
            if (knownRoots.TryGetValue(current, out root))
            {
                break;
            }

            visited.Add(current);
            bool hasGit = HasGitEntry(current, out bool failed);
            probeFailed |= failed;
            if (hasGit)
            {
                root = Path.TrimEndingDirectorySeparator(current);
                break;
            }
        }

        if (!probeFailed)
        {
            foreach (string visitedDirectory in visited)
            {
                knownRoots[visitedDirectory] = root;
            }
        }

        return root;
    }

    private bool HasGitEntry(string directory, out bool failed)
    {
        failed = false;
        try
        {
            File.GetAttributes(Path.Combine(directory, ".git"));
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            failed = true;
            log.LogWarning($"Cannot read '{directory}' while looking for a git repository.", exception);
            return false;
        }
        catch (IOException exception)
        {
            failed = true;
            log.LogWarning($"Cannot read '{directory}' while looking for a git repository.", exception);
            return false;
        }
    }
}
