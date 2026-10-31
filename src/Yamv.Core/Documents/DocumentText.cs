using System.Text;

namespace Yamv.Core.Documents;

/// <summary>
/// Reads a document's text as the window's reader: decoding matches <see cref="File.ReadAllText(string)"/>, while the open
/// share lets another program write, rename or delete the file during the read instead of failing on a sharing violation.
/// </summary>
public static class DocumentText
{
    /// <summary>
    /// Reads <paramref name="path"/> whole through <see cref="OpenShared"/>, decoding by its byte order mark and defaulting
    /// to UTF-8, which is what <see cref="File.ReadAllText(string)"/> does.
    /// </summary>
    /// <param name="path">The document's full path.</param>
    /// <returns>The document's text.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">The file's folder does not exist.</exception>
    /// <exception cref="IOException">The read failed for another reason.</exception>
    /// <exception cref="UnauthorizedAccessException">The file's permissions refuse the read.</exception>
    public static string Read(string path)
    {
        using FileStream stream = OpenShared(path);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Opens <paramref name="path"/> for reading while another program may read, write, rename or delete it at the same
    /// time: an editor's save-by-rename or a git checkout does not wait on the window's read.
    /// </summary>
    /// <param name="path">The file's full path.</param>
    /// <returns>The open stream, positioned at the start.</returns>
    internal static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
