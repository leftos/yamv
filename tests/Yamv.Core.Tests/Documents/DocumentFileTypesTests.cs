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
}
