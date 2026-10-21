namespace Yamv.Core.Documents;

/// <summary>A document's metadata without opening it: its length and the UTC time it was last written.</summary>
/// <param name="Length">The document's length in bytes.</param>
/// <param name="LastWriteUtc">The UTC time the document was last written.</param>
internal sealed record FileStamp(long Length, DateTime LastWriteUtc);

/// <summary>Reads a document's stamp from the file system's metadata; a missing document has no stamp.</summary>
internal static class FileStamps
{
    public static FileStamp? Read(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : null;
    }
}
