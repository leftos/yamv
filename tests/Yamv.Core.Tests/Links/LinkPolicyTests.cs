using Yamv.Core.Links;

namespace Yamv.Core.Tests.Links;

public sealed class LinkPolicyTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "yamv-link-root");

    private static readonly LinkContext Context = new(Root, ExistingFile);

    [Fact]
    public void Decide_Http_OpensExternal()
    {
        Uri uri = new("http://example.com/a");

        OpenExternal decision = Assert.IsType<OpenExternal>(LinkPolicy.Decide(uri, Context));

        Assert.Equal(uri, decision.Uri);
    }

    [Fact]
    public void Decide_UppercaseHttps_OpensExternal()
    {
        OpenExternal decision = Assert.IsType<OpenExternal>(LinkPolicy.Decide(new Uri("HTTPS://example.com/a"), Context));

        Assert.Equal("https", decision.Uri.Scheme);
    }

    [Fact]
    public void Decide_MailtoWithQuery_OpensExternal()
    {
        Uri uri = new("mailto:someone@example.com?subject=hello");

        Assert.IsType<OpenExternal>(LinkPolicy.Decide(uri, Context));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,hello")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("ms-settings:network")]
    [InlineData("ftp://example.com/a.png")]
    public void Decide_OtherScheme_IsIgnored(string href)
    {
        Ignore decision = Assert.IsType<Ignore>(LinkPolicy.Decide(new Uri(href), Context));

        Assert.NotEmpty(decision.Reason);
    }

    [Fact]
    public void Decide_MarkdownInsideTheRoot_OpensTheDocument()
    {
        string path = Path.Combine(Root, "notes.md");

        OpenDocument decision = Assert.IsType<OpenDocument>(LinkPolicy.Decide(FileUri(path), Context));

        Assert.Equal(path, decision.Path);
        Assert.Null(decision.Fragment);
    }

    [Fact]
    public void Decide_MarkdownOutsideTheRoot_OpensTheDocument()
    {
        string path = Path.Combine(Path.GetTempPath(), "yamv-link-elsewhere", "notes.md");

        OpenDocument decision = Assert.IsType<OpenDocument>(LinkPolicy.Decide(FileUri(path), Context));

        Assert.Equal(path, decision.Path);
    }

    [Fact]
    public void Decide_MissingMarkdownInsideTheRoot_OpensTheDocument()
    {
        string path = Path.Combine(Root, "missing", "notes.md");
        var context = new LinkContext(Root, MissingPath);

        OpenDocument decision = Assert.IsType<OpenDocument>(LinkPolicy.Decide(FileUri(path), context));

        Assert.Equal(path, decision.Path);
    }

    [Fact]
    public void Decide_MarkdownWithAnEncodedFragment_OpensTheDocumentAtTheDecodedHeading()
    {
        Uri link = new(FileUri(Path.Combine(Root, "notes.md")).AbsoluteUri + "#Heading%20Two");

        OpenDocument decision = Assert.IsType<OpenDocument>(LinkPolicy.Decide(link, Context));

        Assert.Equal(Path.Combine(Root, "notes.md"), decision.Path);
        Assert.Equal("Heading Two", decision.Fragment);
    }

    [Fact]
    public void Decide_MarkdownWithAnEmptyFragment_OpensTheDocumentAtNoHeading()
    {
        Uri link = new(FileUri(Path.Combine(Root, "notes.md")).AbsoluteUri + "#");

        OpenDocument decision = Assert.IsType<OpenDocument>(LinkPolicy.Decide(link, Context));

        Assert.Null(decision.Fragment);
    }

    [Theory]
    [InlineData("notes.MARKDOWN")]
    [InlineData("notes.TXT")]
    public void Decide_DocumentExtensionInAnotherCase_OpensTheDocument(string name)
    {
        string path = Path.Combine(Root, name);

        Assert.IsType<OpenDocument>(LinkPolicy.Decide(FileUri(path), Context));
    }

    [Theory]
    [InlineData("a.png")]
    [InlineData("report.PDF")]
    [InlineData("clip.mp4")]
    [InlineData("config.yaml")]
    public void Decide_ViewableTypeInsideTheRoot_OpensTheRealPathWithTheDefaultApp(string name)
    {
        string link = Path.Combine(Root, name);
        string real = Path.Combine(Root, "real", name);

        OpenWithDefaultApp decision = Assert.IsType<OpenWithDefaultApp>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(real))));

        Assert.Equal(real, decision.Path);
    }

    [Theory]
    [InlineData("run.bat")]
    [InlineData("setup.EXE")]
    [InlineData("x.py")]
    [InlineData("x.wsb")]
    [InlineData("x.rdp")]
    [InlineData("x.xll")]
    [InlineData("tool")]
    public void Decide_UnlistedTypeInsideTheRoot_AsksBeforeOpeningTheRealPath(string name)
    {
        string link = Path.Combine(Root, name);
        string real = Path.Combine(Root, "real", name);

        Confirm decision = Assert.IsType<Confirm>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(real))));

        Assert.Equal(real, decision.Path);
    }

    [Fact]
    public void Decide_FolderInsideTheRoot_AsksFirstEvenWithAViewableName()
    {
        string link = Path.Combine(Root, "photos.png");
        var context = new LinkContext(
            Root,
            path => new ResolvedPath(path, Exists: true, IsDirectory: path.Equals(link, StringComparison.OrdinalIgnoreCase))
        );

        Confirm decision = Assert.IsType<Confirm>(LinkPolicy.Decide(FileUri(link), context));

        Assert.Equal(link, decision.Path);
    }

    [Fact]
    public void Decide_ViewableLinkNameToAnUnlistedTarget_AsksNamingTheTarget()
    {
        string link = Path.Combine(Root, "a.png");
        string target = Path.Combine(Root, "evil.exe");

        Confirm decision = Assert.IsType<Confirm>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(target))));

        Assert.Equal(target, decision.Path);
    }

    [Fact]
    public void Decide_UnlistedLinkNameToAViewableTarget_AsksFirst()
    {
        string link = Path.Combine(Root, "evil.bat");
        string target = Path.Combine(Root, "a.png");

        Confirm decision = Assert.IsType<Confirm>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(target))));

        Assert.Equal(target, decision.Path);
    }

    [Fact]
    public void Decide_ViewableLinkNameToAViewableTarget_OpensTheTargetWithTheDefaultApp()
    {
        string link = Path.Combine(Root, "a.png");
        string target = Path.Combine(Root, "b.png");

        OpenWithDefaultApp decision = Assert.IsType<OpenWithDefaultApp>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(target))));

        Assert.Equal(target, decision.Path);
    }

    [Fact]
    public void Decide_MissingFileInsideTheRoot_IsIgnored()
    {
        var context = new LinkContext(
            Root,
            path => new ResolvedPath(path, Exists: !path.EndsWith("a.png", StringComparison.Ordinal), IsDirectory: false)
        );

        Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(Path.Combine(Root, "a.png")), context));
    }

    [Fact]
    public void Decide_ImageOutsideTheRoot_IsIgnored()
    {
        string path = Path.Combine(Path.GetTempPath(), "yamv-link-elsewhere", "a.png");

        Ignore decision = Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(path), Context));

        Assert.NotEmpty(decision.Reason);
    }

    [Theory]
    [InlineData("notes.md")]
    [InlineData("a.png")]
    public void Decide_PathTheResolverCannotResolve_IsIgnored(string name)
    {
        var context = new LinkContext(Root, _ => null);

        Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(Path.Combine(Root, name)), context));
    }

    [Fact]
    public void Decide_NulCharacterInThePath_IsIgnored()
    {
        Uri link = new(FileUri(Root).AbsoluteUri.TrimEnd('/') + "/a%00.png");

        Assert.IsType<Ignore>(LinkPolicy.Decide(link, Context));
    }

    [Theory]
    [InlineData("notes.md.")]
    [InlineData("notes.md%20")]
    public void Decide_DocumentNameWithATrailingDotOrSpaceOnWindows_OpensTheDocument(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows drops trailing dots and spaces from a file name.");
        Uri link = new(FileUri(Root).AbsoluteUri.TrimEnd('/') + "/" + name);

        OpenDocument decision = Assert.IsType<OpenDocument>(LinkPolicy.Decide(link, Context));

        Assert.Equal(Path.Combine(Root, "notes.md"), decision.Path);
    }

    [Fact]
    public void Decide_AlternateDataStreamOnWindows_IsIgnored()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only NTFS names a stream after a ':' in a file name.");
        Uri link = new(FileUri(Root).AbsoluteUri.TrimEnd('/') + "/a.png:evil.exe");

        Assert.IsType<Ignore>(LinkPolicy.Decide(link, Context));
    }

    [Theory]
    [InlineData("file://server/share/a.png")]
    [InlineData(@"\\server\share\a.png")]
    [InlineData("file:///%5C%5Cserver/share/a.png")]
    [InlineData("file:///%5C%5C%3F%5CC:%5Cx%5Ca.png")]
    [InlineData("file:///%5C%5C.%5Cpipe%5Cx")]
    [InlineData("file:///%5C%3F%3F%5CC:%5CWindows%5Cnotepad.exe")]
    [InlineData("file:///%5C%5C%3F%5CVolume%7B01234567-89ab-cdef-0123-456789abcdef%7D%5Ca.png")]
    public void Decide_NetworkShareOrDevicePath_IsIgnoredWithoutResolvingIt(string href)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Network share and device paths exist only on Windows.");
        Assert.True(Uri.TryCreate(href, UriKind.Absolute, out Uri? link), $"'{href}' is not an absolute URI.");
        int resolverCalls = 0;
        var context = new LinkContext(
            Root,
            path =>
            {
                resolverCalls++;
                return ExistingFile(path);
            }
        );

        Assert.IsType<Ignore>(LinkPolicy.Decide(link, context));

        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public void Decide_DocumentThatResolvesToANetworkShare_IsIgnored()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Network share paths exist only on Windows.");
        string link = Path.Combine(Root, "notes.md");

        Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(@"\\server\share\notes.md"))));
    }

    [Fact]
    public void Decide_FileLinkWithAHost_IsIgnored()
    {
        Assert.IsType<Ignore>(LinkPolicy.Decide(new Uri("file://server/share/a.png"), Context));
        Assert.IsType<Ignore>(LinkPolicy.Decide(new Uri("file://server/share/a.md"), Context));
        Assert.IsType<Ignore>(LinkPolicy.Decide(new Uri("//server/share/a.png"), Context));
    }

    [Fact]
    public void Decide_SiblingFolderThatSharesTheRootsPrefix_IsIgnored()
    {
        string path = Path.Combine(Root + "-other", "a.png");

        Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(path), Context));
    }

    [Fact]
    public void Decide_DotDotEscapingTheRoot_IsIgnored()
    {
        string path = Path.Combine(Root, "..", "secret.png");

        Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(path), Context));
    }

    [Fact]
    public void Decide_EncodedDotDotEscapingTheRoot_IsIgnored()
    {
        Uri link = new(FileUri(Root).AbsoluteUri.TrimEnd('/') + "/%2e%2e/secret.png");

        Assert.IsType<Ignore>(LinkPolicy.Decide(link, Context));
    }

    [Fact]
    public void Decide_InsidePathThatResolvesOutside_IsIgnored()
    {
        string inside = Path.Combine(Root, "a.png");
        string outside = Path.Combine(Path.GetTempPath(), "yamv-link-elsewhere", "a.png");

        Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(inside), Mapping(inside, ExistingFile(outside))));
    }

    [Fact]
    public void Decide_OutsidePathThatResolvesInside_OpensTheInsidePathWithTheDefaultApp()
    {
        string outward = Path.Combine(Path.GetTempPath(), "yamv-link-elsewhere", "a.png");
        string inside = Path.Combine(Root, "a.png");

        OpenWithDefaultApp decision = Assert.IsType<OpenWithDefaultApp>(LinkPolicy.Decide(FileUri(outward), Mapping(outward, ExistingFile(inside))));

        Assert.Equal(inside, decision.Path);
    }

    [Fact]
    public void Decide_NoResolvedUri_IsIgnored() => Assert.IsType<Ignore>(LinkPolicy.Decide(null, Context));

    [Fact]
    public void Decide_RelativeUri_IsIgnored() => Assert.IsType<Ignore>(LinkPolicy.Decide(new Uri("docs/a.md", UriKind.Relative), Context));

    private static ResolvedPath ExistingFile(string path) => new(path, Exists: true, IsDirectory: false);

    private static ResolvedPath MissingPath(string path) => new(path, Exists: false, IsDirectory: false);

    /// <summary>A context whose resolver sends <paramref name="link"/> to <paramref name="real"/> and every other path to itself.</summary>
    private static LinkContext Mapping(string link, ResolvedPath real) =>
        new(Root, path => path.Equals(link, StringComparison.OrdinalIgnoreCase) ? real : ExistingFile(path));

    private static Uri FileUri(string path) => new(path, UriKind.Absolute);
}
