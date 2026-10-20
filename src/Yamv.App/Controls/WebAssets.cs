using System.Reflection;
using Yamv.Core.Diagnostics;

namespace Yamv.App.Controls;

/// <summary>
/// Puts the document page's embedded files (HTML, CSS, JS, fonts, vendor libraries) on disk, so the web view can load
/// them as <c>file://</c> URLs. Each build gets its own folder, named by the assembly's module version id, under the
/// local cache directory. Several YAMV processes may start at once: each extracts into a private sibling folder and
/// renames it into place, and a folder that already holds the completion marker is used as it is, never rewritten.
/// Every failure is logged with the folder it concerns, and no staging folder is left behind unless it is in use.
/// </summary>
internal static class WebAssets
{
    private const string ResourcePrefix = "Web/";
    private const string MarkerFileName = ".complete";
    private static readonly Lock Gate = new();
    private static string? _folder;

    /// <summary>The <c>file://</c> URL of the page template, extracting the assets on first use.</summary>
    public static Uri TemplateUri(IAppLog log) => new(Path.Combine(Folder(log), "template.html"));

    private static string Folder(IAppLog log)
    {
        lock (Gate)
        {
            _folder ??= Extract(log);
            return _folder;
        }
    }

    /// <summary>
    /// Extracts into a staging folder and renames it into place. When the rename fails, the final folder is used only
    /// if it holds the marker (another process finished first); otherwise the staging copy is deleted and the whole
    /// extraction runs once more, and when that fails too this process keeps its second staging copy. A failure to
    /// write deletes the staging folder and is rethrown.
    /// </summary>
    private static string Extract(IAppLog log)
    {
        const int Attempts = 2;
        Assembly assembly = typeof(WebAssets).Assembly;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YAMV", "web");
        string final = Path.Combine(root, assembly.ManifestModule.ModuleVersionId.ToString("N"));
        if (HasMarker(final))
        {
            return final;
        }

        for (int attempt = 1; ; attempt++)
        {
            string staging = WriteStaging(log, assembly, final);
            if (TryMove(log, staging, final))
            {
                return final;
            }

            if (HasMarker(final))
            {
                DeleteStaging(log, staging);
                return final;
            }

            if (attempt == Attempts)
            {
                log.LogWarning($"{final} still has no completion marker; using the private copy {staging}");
                return staging;
            }

            log.LogWarning($"{final} exists without its completion marker; extracting the page assets again");
            DeleteStaging(log, staging);
        }
    }

    private static bool HasMarker(string folder) => File.Exists(Path.Combine(folder, MarkerFileName));

    private static string WriteStaging(IAppLog log, Assembly assembly, string final)
    {
        string staging = $"{final}.tmp-{Guid.NewGuid():N}";
        try
        {
            WriteResources(assembly, staging);
            return staging;
        }
        catch (Exception exception)
        {
            log.LogError($"Could not extract the document page assets into {staging}", exception);
            DeleteStaging(log, staging);
            throw;
        }
    }

    /// <summary>Renames <paramref name="staging"/> to <paramref name="final"/>; false when the final folder is in the way.</summary>
    private static bool TryMove(IAppLog log, string staging, string final)
    {
        try
        {
            Directory.Move(staging, final);
            log.LogInfo($"Extracted the document page assets to {final}");
            return true;
        }
        catch (IOException exception)
        {
            log.LogWarning($"Could not move the page assets from {staging} to {final}", exception);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            log.LogError($"Could not move the page assets from {staging} to {final}", exception);
            DeleteStaging(log, staging);
            throw;
        }
    }

    private static void DeleteStaging(IAppLog log, string staging)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.LogWarning($"Could not remove the page asset copy {staging}", exception);
        }
    }

    private static void WriteResources(Assembly assembly, string folder)
    {
        int written = 0;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            string normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string relative = normalized[ResourcePrefix.Length..];
            string target = Path.GetFullPath(Path.Combine(folder, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream source =
                assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Embedded resource {name} could not be opened.");
            using FileStream destination = File.Create(target);
            source.CopyTo(destination);
            written++;
        }

        if (written == 0)
        {
            throw new InvalidOperationException("No embedded Web/ resources found; check the EmbeddedResource items in Yamv.App.csproj.");
        }

        File.WriteAllText(Path.Combine(folder, MarkerFileName), $"{written}\n");
    }
}
