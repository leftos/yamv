using System.Text;
using Yamv.Core.Documents;

namespace Yamv.Core.Tests.Documents;

public sealed class DocumentTextTests : IDisposable
{
    private const string Sample = "café ✓";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "yamv-text-" + Guid.NewGuid().ToString("N"));

    private string Document => Path.Combine(_directory, "readme.md");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void A_file_held_open_for_reading_can_be_renamed_by_another_program()
    {
        string path = WriteSample();

        using (DocumentText.OpenShared(path))
        {
            File.Move(path, path + ".moved");

            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public void A_file_held_open_for_reading_can_be_deleted_by_another_program()
    {
        string path = WriteSample();

        using (DocumentText.OpenShared(path))
        {
            File.Delete(path);

            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public void A_file_held_open_for_reading_can_be_written_by_another_program()
    {
        string path = WriteSample();

        using (DocumentText.OpenShared(path))
        {
            File.WriteAllText(path, "saved by an editor");

            Assert.Equal("saved by an editor", File.ReadAllText(path));
        }
    }

    [Theory]
    [InlineData("utf8-no-bom")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le-bom")]
    [InlineData("utf16-be-bom")]
    [InlineData("empty")]
    public void Read_returns_what_File_ReadAllText_returns(string encoding)
    {
        string path = WriteEncoded(encoding, out string expected);

        Assert.Equal(expected, DocumentText.Read(path));
        Assert.Equal(File.ReadAllText(path), DocumentText.Read(path));
    }

    [Fact]
    public void A_missing_file_throws_FileNotFoundException()
    {
        Directory.CreateDirectory(_directory);

        Assert.Throws<FileNotFoundException>(() => DocumentText.Read(Document));
    }

    [Fact]
    public void A_missing_folder_throws_DirectoryNotFoundException() => Assert.Throws<DirectoryNotFoundException>(() => DocumentText.Read(Document));

    private string WriteSample()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Document, Sample);
        return Document;
    }

    private string WriteEncoded(string encoding, out string expected)
    {
        Directory.CreateDirectory(_directory);
        expected = encoding == "empty" ? string.Empty : Sample;
        File.WriteAllBytes(Document, BytesFor(encoding));
        return Document;
    }

    private static byte[] BytesFor(string encoding)
    {
        UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
        return encoding switch
        {
            "utf8-no-bom" => utf8.GetBytes(Sample),
            "utf8-bom" => [.. Encoding.UTF8.GetPreamble(), .. utf8.GetBytes(Sample)],
            "utf16-le-bom" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Sample)],
            "utf16-be-bom" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(Sample)],
            "empty" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "An unknown encoding."),
        };
    }
}
