using Yamv.Core.Documents;

namespace Yamv.Core.Tests.Documents;

public sealed class DocumentFileTypesTests
{
    [Theory]
    [InlineData("notes.md")]
    [InlineData("notes.MD")]
    [InlineData("notes.Md")]
    [InlineData("notes.markdown")]
    [InlineData("notes.MARKDOWN")]
    [InlineData("notes.mdx")]
    [InlineData("notes.MDX")]
    [InlineData("notes.txt")]
    [InlineData("notes.TXT")]
    public void IsDocument_DocumentFile_IsTrue(string path) => Assert.True(DocumentFileTypes.IsDocument(path));

    [Theory]
    [InlineData("notes.md.bak")]
    [InlineData("notes.docx")]
    [InlineData("README")]
    [InlineData("notes.")]
    [InlineData("")]
    public void IsDocument_OtherFile_IsFalse(string path) => Assert.False(DocumentFileTypes.IsDocument(path));

    [Fact]
    public void IsDocument_PathWithDotsInItsFolders_IsTrue() =>
        Assert.True(DocumentFileTypes.IsDocument(Path.Combine("releases", "v1.2.3", "notes.md")));

    [Fact]
    public void IsDocument_PathWithDotsInItsFoldersAndNoExtension_IsFalse() =>
        Assert.False(DocumentFileTypes.IsDocument(Path.Combine("releases", "v1.2.3", "README")));

    [Fact]
    public void Patterns_AreTheExtensionsAsPickerPatterns()
    {
        string[] extensions = [".md", ".markdown", ".mdx", ".txt"];
        string[] patterns = ["*.md", "*.markdown", "*.mdx", "*.txt"];

        Assert.Equal(extensions, DocumentFileTypes.Extensions);
        Assert.Equal(patterns, DocumentFileTypes.Patterns);
    }

    [Fact]
    public void DropHint_names_every_document_type() => Assert.Equal("Drop .md, .markdown, .mdx or .txt files", DocumentFileTypes.DropHint);

    [Fact]
    public void Sort_keeps_documents_in_drop_order()
    {
        DroppedFiles sorted = DocumentFileTypes.Sort(["b.md", "c.txt", "a.markdown"]);

        Assert.Equal(["b.md", "c.txt", "a.markdown"], sorted.Documents);
        Assert.Equal(0, sorted.Refused);
    }

    [Fact]
    public void Sort_counts_the_rest_as_refused()
    {
        DroppedFiles sorted = DocumentFileTypes.Sort(["run.exe", "b.md", "tool.ps1", ""]);

        Assert.Equal(["b.md"], sorted.Documents);
        Assert.Equal(3, sorted.Refused);
    }

    [Fact]
    public void Sort_refuses_a_folder_named_like_a_document()
    {
        string folder = Path.Combine(Path.GetTempPath(), "yamv-sort-" + Guid.NewGuid().ToString("N"), "notes.md");
        Directory.CreateDirectory(folder);
        try
        {
            DroppedFiles sorted = DocumentFileTypes.Sort([folder]);

            Assert.Empty(sorted.Documents);
            Assert.Equal(1, sorted.Refused);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public void Sort_of_nothing_is_empty()
    {
        DroppedFiles sorted = DocumentFileTypes.Sort([]);

        Assert.Empty(sorted.Documents);
        Assert.Equal(0, sorted.Refused);
    }
}
