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

    [Fact]
    public void A_mailto_link_keeps_its_address_subject_and_body()
    {
        Uri uri = new("mailto:a@b.c?To=d@e.f&CC=g@h.i&bcc=j@k.l&Subject=Hi%20there&body=line");

        OpenExternal decision = Assert.IsType<OpenExternal>(LinkPolicy.Decide(uri, Context));

        Assert.Equal("mailto:a@b.c?To=d@e.f&CC=g@h.i&bcc=j@k.l&Subject=Hi%20there&body=line", decision.Uri.AbsoluteUri);
        Assert.Empty(decision.DroppedFields);
    }

    [Fact]
    public void A_mailto_link_drops_attach_fields()
    {
        Uri uri = new(@"mailto:a@b.c?subject=x&attach=C:\secret&body=y");

        OpenExternal decision = Assert.IsType<OpenExternal>(LinkPolicy.Decide(uri, Context));

        Assert.Equal(new Uri("mailto:a@b.c?subject=x&body=y"), decision.Uri);
        Assert.Equal(["attach"], decision.DroppedFields);
    }

    [Fact]
    public void A_mailto_link_with_only_dropped_fields_keeps_its_address_alone()
    {
        Uri uri = new("mailto:a@b.c?Attachment=x&&other#frag");

        OpenExternal decision = Assert.IsType<OpenExternal>(LinkPolicy.Decide(uri, Context));

        Assert.Equal("mailto:a@b.c", decision.Uri.AbsoluteUri);
        Assert.Equal(["Attachment", "other"], decision.DroppedFields);
    }

    [Fact]
    public void A_mailto_link_with_only_an_attach_field_never_throws()
    {
        OpenExternal decision = Assert.IsType<OpenExternal>(LinkPolicy.Decide(new Uri("mailto:?attach=x"), Context));

        Assert.Equal("mailto:", decision.Uri.AbsoluteUri);
        Assert.Equal(["attach"], decision.DroppedFields);
    }

    [Fact]
    public void An_encoded_attach_inside_a_kept_body_stays_part_of_the_body()
    {
        Uri uri = new("mailto:a@b.c?body=x%26attach%3DC:%5Cs");

        OpenExternal decision = Assert.IsType<OpenExternal>(LinkPolicy.Decide(uri, Context));

        Assert.Equal("mailto:a@b.c?body=x%26attach%3DC:%5Cs", decision.Uri.AbsoluteUri);
        Assert.Contains("%26attach%3D", decision.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Empty(decision.DroppedFields);
    }

    [Fact]
    public void A_mailto_link_with_no_query_is_unchanged()
    {
        Uri uri = new("mailto:a@b.c");

        OpenExternal decision = Assert.IsType<OpenExternal>(LinkPolicy.Decide(uri, Context));

        Assert.Equal(uri, decision.Uri);
        Assert.Empty(decision.DroppedFields);
    }

    [Fact]
    public void An_http_link_drops_no_fields()
    {
        Uri uri = new("https://example.com/a?attach=x");

        OpenExternal decision = Assert.IsType<OpenExternal>(LinkPolicy.Decide(uri, Context));

        Assert.Equal(uri, decision.Uri);
        Assert.Empty(decision.DroppedFields);
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
        Assert.Equal(IgnoreKind.Scheme, decision.Kind);
    }

    [Fact]
    public void Decide_MarkdownInsideTheRoot_OpensTheDocument()
    {
        string path = Path.Combine(Root, "notes.md");

        OpenDocument decision = Assert.IsType<OpenDocument>(LinkPolicy.Decide(FileUri(path), Context));

        Assert.Equal(new OpenDocument(path, path, Fragment: null), decision);
    }

    [Fact]
    public void Decide_DocumentThroughALink_OpensTheRealPath()
    {
        string link = Path.Combine(Root, "notes.md");
        string real = Path.Combine(Root, "real", "other.md");

        OpenDocument decision = Assert.IsType<OpenDocument>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(real))));

        Assert.Equal(new OpenDocument(real, link, Fragment: null), decision);
    }

    [Fact]
    public void Decide_DocumentNameLinkingToAnotherType_IsNotOpenedAsADocument()
    {
        string link = Path.Combine(Root, "README.md");
        string real = Path.Combine(Root, "tool.exe");

        Confirm decision = Assert.IsType<Confirm>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(real))));

        Assert.Equal(real, decision.Path);
    }

    [Fact]
    public void Decide_DocumentNameLinkingToAFolder_IsNotOpenedAsADocument()
    {
        string link = Path.Combine(Root, "notes.md");
        string real = Path.Combine(Root, "folder.md");

        Confirm decision = Assert.IsType<Confirm>(
            LinkPolicy.Decide(FileUri(link), Mapping(link, new ResolvedPath(real, Exists: true, IsDirectory: true)))
        );

        Assert.Equal(real, decision.Path);
    }

    [Fact]
    public void Decide_MarkdownOutsideTheRoot_OpensTheDocument()
    {
        string path = Path.Combine(Path.GetTempPath(), "yamv-link-elsewhere", "notes.md");

        OpenDocument decision = Assert.IsType<OpenDocument>(LinkPolicy.Decide(FileUri(path), Context));

        Assert.Equal(new OpenDocument(path, path, Fragment: null), decision);
    }

    [Fact]
    public void Decide_MissingMarkdownInsideTheRoot_OpensTheDocument()
    {
        string path = Path.Combine(Root, "missing", "notes.md");
        var context = new LinkContext(Root, MissingPath);

        OpenDocument decision = Assert.IsType<OpenDocument>(LinkPolicy.Decide(FileUri(path), context));

        Assert.Equal(new OpenDocument(path, path, Fragment: null), decision);
    }

    [Fact]
    public void Decide_MarkdownWithAnEncodedFragment_OpensTheDocumentAtTheDecodedHeading()
    {
        Uri link = new(FileUri(Path.Combine(Root, "notes.md")).AbsoluteUri + "#Heading%20Two");

        OpenDocument decision = Assert.IsType<OpenDocument>(LinkPolicy.Decide(link, Context));

        string path = Path.Combine(Root, "notes.md");
        Assert.Equal(new OpenDocument(path, path, "Heading Two"), decision);
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

        Assert.Equal(IgnoreKind.Missing, Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(Path.Combine(Root, "a.png")), context)).Kind);
    }

    [Fact]
    public void Decide_ImageOutsideTheRoot_IsIgnored()
    {
        string path = Path.Combine(Path.GetTempPath(), "yamv-link-elsewhere", "a.png");

        Ignore decision = Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(path), Context));

        Assert.NotEmpty(decision.Reason);
        Assert.Equal(IgnoreKind.OutsideRoot, decision.Kind);
    }

    [Theory]
    [InlineData("notes.md")]
    [InlineData("a.png")]
    public void Decide_PathTheResolverCannotResolve_IsIgnored(string name)
    {
        var context = new LinkContext(Root, _ => null);

        Assert.Equal(IgnoreKind.NotPlainPath, Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(Path.Combine(Root, name)), context)).Kind);
    }

    [Fact]
    public void Decide_NulCharacterInThePath_IsIgnored()
    {
        Uri link = new(FileUri(Root).AbsoluteUri.TrimEnd('/') + "/a%00.png");

        Assert.Equal(IgnoreKind.NotPlainPath, Assert.IsType<Ignore>(LinkPolicy.Decide(link, Context)).Kind);
    }

    [Theory]
    [InlineData("X:")]
    [InlineData(@"relative\p.png")]
    public void A_real_path_that_is_not_local_and_fully_qualified_is_ignored(string real)
    {
        string link = Path.Combine(Root, "p.png");
        List<string> normalised = [];

        Ignore decision = Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(real)), normalised.Add));

        Assert.Equal(IgnoreKind.NotPlainPath, decision.Kind);
        Assert.DoesNotContain(real, normalised);
    }

    [Fact]
    public void A_real_path_with_a_segment_ending_in_a_dot_is_ignored()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows rewrites a name ending in a dot.");
        string link = Path.Combine(Root, "p.png");

        Ignore decision = Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(@"X:\r\dir.\p.png"))));

        Assert.Equal(IgnoreKind.TrailingDotOrSpace, decision.Kind);
    }

    [Theory]
    [InlineData("p.png", @"X:\r\p.png ")]
    [InlineData("notes.md", @"X:\r\notes.md.")]
    public void A_real_path_with_a_segment_ending_in_a_space_is_ignored(string name, string real)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows rewrites a name ending in a dot or space.");
        string link = Path.Combine(Root, name);

        Ignore decision = Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(real))));

        Assert.Equal(IgnoreKind.TrailingDotOrSpace, decision.Kind);
    }

    [Fact]
    public void A_real_path_Windows_would_rewrite_is_ignored()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows turns a name such as nul into a device.");
        Assert.SkipUnless(
            !string.Equals(Path.GetFullPath(@"X:\r\nul"), @"X:\r\nul", StringComparison.Ordinal),
            "This Windows version no longer rewrites a folder's nul into the device."
        );
        string link = Path.Combine(Root, "a.png");

        Ignore decision = Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(@"X:\r\nul"))));

        Assert.Equal(IgnoreKind.NotPlainPath, decision.Kind);
    }

    [Theory]
    [InlineData("notes.md.")]
    [InlineData("notes.md%20")]
    public void Decide_DocumentNameWithATrailingDotOrSpaceOnWindows_OpensTheDocument(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows drops trailing dots and spaces from a file name.");
        Uri link = new(FileUri(Root).AbsoluteUri.TrimEnd('/') + "/" + name);

        OpenDocument decision = Assert.IsType<OpenDocument>(LinkPolicy.Decide(link, Context));

        string path = Path.Combine(Root, "notes.md");
        Assert.Equal(new OpenDocument(path, path, Fragment: null), decision);
    }

    [Fact]
    public void Decide_AlternateDataStreamOnWindows_IsIgnored()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only NTFS names a stream after a ':' in a file name.");
        Uri link = new(FileUri(Root).AbsoluteUri.TrimEnd('/') + "/a.png:evil.exe");

        Assert.Equal(IgnoreKind.NotPlainPath, Assert.IsType<Ignore>(LinkPolicy.Decide(link, Context)).Kind);
    }

    [Theory]
    [InlineData("file://server/share/a.png", IgnoreKind.NetworkShare)]
    [InlineData(@"\\server\share\a.png", IgnoreKind.NetworkShare)]
    [InlineData("file:///%5C%5Cserver/share/a.png", IgnoreKind.NotPlainPath)]
    [InlineData("file:///%5C%5C%3F%5CC:%5Cx%5Ca.png", IgnoreKind.NotPlainPath)]
    [InlineData("file:///%5C%5C.%5Cpipe%5Cx", IgnoreKind.NotPlainPath)]
    [InlineData("file:///%5C%3F%3F%5CC:%5CWindows%5Cnotepad.exe", IgnoreKind.NotPlainPath)]
    [InlineData("file:///%5C%5C%3F%5CVolume%7B01234567-89ab-cdef-0123-456789abcdef%7D%5Ca.png", IgnoreKind.NotPlainPath)]
    public void Decide_NetworkShareOrDevicePath_IsIgnoredWithoutResolvingIt(string href, IgnoreKind kind)
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

        Assert.Equal(kind, Assert.IsType<Ignore>(LinkPolicy.Decide(link, context)).Kind);

        Assert.Equal(0, resolverCalls);
    }

    [Theory]
    [InlineData(@"\\host\share\x.png", true)]
    [InlineData(@"\\.host\s\x", true)]
    [InlineData(@"\\?host\s\x", true)]
    [InlineData("//host/share/x.png", true)]
    [InlineData(@"\\?\C:\x.png", false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData(@"\??\C:\x.png", false)]
    [InlineData("///host/share/x.png", false)]
    [InlineData("///?/C:/x.png", false)]
    [InlineData("//??/C:/x.png", false)]
    [InlineData(@"C:\x.png", false)]
    public void A_share_or_device_path_is_a_network_share_only_when_it_names_a_host(string path, bool share)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Network share and device paths exist only on Windows.");

        Assert.Equal(share, RealPath.IsNetworkShare(path));
    }

    [Theory]
    [InlineData("file://server/share/a.png")]
    [InlineData(@"\\server\share\a.png")]
    [InlineData("file:///%5C%5Cserver/share/a.png")]
    [InlineData("file:///%5C%5Chost/share/a~1.md")]
    [InlineData("file:///%5C%5C%3F%5CC:%5Cx%5Ca.png")]
    [InlineData("file:///%5C%5C.%5Cpipe%5Cx")]
    [InlineData("file:///%5C%3F%3F%5CC:%5CWindows%5Cnotepad.exe")]
    [InlineData("file:///%5C%5C%3F%5CVolume%7B01234567-89ab-cdef-0123-456789abcdef%7D%5Ca.png")]
    public void Decide_NetworkShareOrDevicePath_IsIgnoredWithoutNormalisingIt(string href)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Network share and device paths exist only on Windows.");
        Assert.True(Uri.TryCreate(href, UriKind.Absolute, out Uri? link), $"'{href}' is not an absolute URI.");
        List<string> normalised = [];

        Assert.IsType<Ignore>(LinkPolicy.Decide(link, Context, normalised.Add));

        Assert.DoesNotContain(normalised, RealPath.IsUncOrDevice);
    }

    [Fact]
    public void Decide_DocumentThatResolvesToANetworkShare_IsIgnored()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Network share paths exist only on Windows.");
        string link = Path.Combine(Root, "notes.md");

        Ignore decision = Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(link), Mapping(link, ExistingFile(@"\\server\share\notes.md"))));

        Assert.Equal(IgnoreKind.NotPlainPath, decision.Kind);
    }

    [Theory]
    [InlineData("file://server/share/a.png")]
    [InlineData("file://server/share/a.md")]
    [InlineData("//server/share/a.png")]
    public void Decide_FileLinkWithAHost_IsIgnored(string href) =>
        Assert.Equal(IgnoreKind.NetworkShare, Assert.IsType<Ignore>(LinkPolicy.Decide(new Uri(href), Context)).Kind);

    [Fact]
    public void Decide_SiblingFolderThatSharesTheRootsPrefix_IsIgnored()
    {
        string path = Path.Combine(Root + "-other", "a.png");

        Assert.Equal(IgnoreKind.OutsideRoot, Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(path), Context)).Kind);
    }

    [Fact]
    public void Decide_DotDotEscapingTheRoot_IsIgnored()
    {
        string path = Path.Combine(Root, "..", "secret.png");

        Assert.Equal(IgnoreKind.OutsideRoot, Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(path), Context)).Kind);
    }

    [Fact]
    public void Decide_EncodedDotDotEscapingTheRoot_IsIgnored()
    {
        Uri link = new(FileUri(Root).AbsoluteUri.TrimEnd('/') + "/%2e%2e/secret.png");

        Assert.Equal(IgnoreKind.OutsideRoot, Assert.IsType<Ignore>(LinkPolicy.Decide(link, Context)).Kind);
    }

    [Fact]
    public void Decide_InsidePathThatResolvesOutside_IsIgnored()
    {
        string inside = Path.Combine(Root, "a.png");
        string outside = Path.Combine(Path.GetTempPath(), "yamv-link-elsewhere", "a.png");

        Ignore decision = Assert.IsType<Ignore>(LinkPolicy.Decide(FileUri(inside), Mapping(inside, ExistingFile(outside))));

        Assert.Equal(IgnoreKind.OutsideRoot, decision.Kind);
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
    public void Decide_NoResolvedUri_IsIgnored() =>
        Assert.Equal(IgnoreKind.NotPlainPath, Assert.IsType<Ignore>(LinkPolicy.Decide(null, Context)).Kind);

    [Fact]
    public void Decide_RelativeUri_IsIgnored() =>
        Assert.Equal(IgnoreKind.NotPlainPath, Assert.IsType<Ignore>(LinkPolicy.Decide(new Uri("docs/a.md", UriKind.Relative), Context)).Kind);

    private static ResolvedPath ExistingFile(string path) => new(path, Exists: true, IsDirectory: false);

    private static ResolvedPath MissingPath(string path) => new(path, Exists: false, IsDirectory: false);

    /// <summary>A context whose resolver sends <paramref name="link"/> to <paramref name="real"/> and every other path to itself.</summary>
    private static LinkContext Mapping(string link, ResolvedPath real) =>
        new(Root, path => path.Equals(link, StringComparison.OrdinalIgnoreCase) ? real : ExistingFile(path));

    private static Uri FileUri(string path) => new(path, UriKind.Absolute);
}
