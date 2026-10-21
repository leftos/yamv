using Yamv.Core.Widths;

namespace Yamv.Core.Tests.Widths;

public sealed class WidthScopesTests
{
    private static readonly string Dev = P("dev");
    private static readonly string Repo = P("dev", "yamv");
    private static readonly string Docs = Path.Combine(Repo, "docs");
    private static readonly string Plans = Path.Combine(Docs, "plans");
    private static readonly string PlanDoc = Path.Combine(Plans, "v1-build.md");
    private static readonly string Readme = Path.Combine(Repo, "README.md");
    private static readonly string Downloads = P("Users", "me", "Downloads");
    private static readonly string Notes = Path.Combine(Downloads, "notes.md");

    [Fact]
    public void Document_in_a_subfolder_of_the_group_root_has_three_segments()
    {
        var scopes = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, new WidthSource(Plans));

        Assert.Equal(["docs/plans", "yamv (repo)", "All documents"], Labels(scopes));
        Assert.Equal([Plans, Repo, null], Folders(scopes));
        Assert.Equal(0, SelectedIndex(scopes));
    }

    [Fact]
    public void Document_directly_in_the_group_root_hides_the_middle_segment()
    {
        var scopes = WidthScopes.For(Readme, Repo, groupIsRepo: true, new WidthSource(Repo));

        Assert.Equal(["yamv (repo)", "All documents"], Labels(scopes));
        Assert.Equal([Repo, null], Folders(scopes));
        Assert.Equal(0, SelectedIndex(scopes));
    }

    [Fact]
    public void Viewer_document_has_its_own_folder_and_all_documents()
    {
        var scopes = WidthScopes.For(Notes, Downloads, groupIsRepo: false, WidthSource.AppDefault);

        Assert.Equal(["Downloads", "All documents"], Labels(scopes));
        Assert.Equal([Downloads, null], Folders(scopes));
        Assert.Equal(1, SelectedIndex(scopes));
    }

    [Fact]
    public void Source_in_an_intermediate_folder_gets_its_own_segment_selected()
    {
        var scopes = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, new WidthSource(Docs));

        Assert.Equal(["plans", "docs", "yamv (repo)", "All documents"], Labels(scopes));
        Assert.Equal([Plans, Docs, Repo, null], Folders(scopes));
        Assert.Equal(1, SelectedIndex(scopes));
        Assert.Null(scopes.InheritedFrom);
    }

    [Fact]
    public void Source_above_the_group_root_gets_its_segment_before_all_documents()
    {
        var scopes = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, new WidthSource(Dev));

        Assert.Equal(["docs/plans", "yamv (repo)", "dev", "All documents"], Labels(scopes));
        Assert.Equal([Plans, Repo, Dev, null], Folders(scopes));
        Assert.Equal(2, SelectedIndex(scopes));
    }

    [Fact]
    public void App_default_source_selects_all_documents()
    {
        var scopes = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, WidthSource.AppDefault);

        Assert.Equal(["docs/plans", "yamv (repo)", "All documents"], Labels(scopes));
        Assert.True(scopes.Selected.IsAllDocuments);
        Assert.Null(scopes.InheritedFrom);
    }

    [Fact]
    public void Group_root_that_is_not_a_repo_is_labelled_by_its_name()
    {
        var scopes = WidthScopes.For(PlanDoc, Repo, groupIsRepo: false, WidthSource.AppDefault);

        Assert.Equal(["docs/plans", "yamv", "All documents"], Labels(scopes));
    }

    [Fact]
    public void Caption_is_hidden_when_the_selected_segment_holds_the_width()
    {
        var scopes = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, new WidthSource(Plans));

        Assert.Null(scopes.InheritedFrom);
    }

    [Fact]
    public void Caption_names_the_segment_the_selected_folder_inherits_from()
    {
        var opened = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, new WidthSource(Docs));

        WidthScopes scopes = opened.Select(opened.Segments[0], new WidthSource(Docs));

        Assert.Equal(0, SelectedIndex(scopes));
        Assert.Equal("docs", scopes.InheritedFrom);
        Assert.Equal(Plans, scopes.Selected.Folder);
    }

    [Fact]
    public void Caption_names_all_documents_when_the_selected_folder_inherits_the_app_default()
    {
        var opened = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, WidthSource.AppDefault);

        WidthScopes scopes = opened.Select(opened.Segments[1], WidthSource.AppDefault);

        Assert.Equal("All documents", scopes.InheritedFrom);
    }

    [Fact]
    public void Caption_names_a_folder_that_is_no_segment_by_its_name()
    {
        var opened = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, new WidthSource(Plans));

        WidthScopes scopes = opened.Select(opened.Segments[0], new WidthSource(Docs));

        Assert.Equal("docs", scopes.InheritedFrom);
    }

    [Fact]
    public void Reset_is_enabled_only_with_an_own_width_on_the_selected_segment()
    {
        var own = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, new WidthSource(Plans));
        WidthScopes inherited = own.Select(own.Segments[0], new WidthSource(Repo));
        WidthScopes allDocuments = own.Select(own.Segments[2], WidthSource.AppDefault);

        Assert.True(own.CanReset);
        Assert.False(inherited.CanReset);
        Assert.False(allDocuments.CanReset);
    }

    [Fact]
    public void Clear_all_target_is_the_group_root()
    {
        var scopes = WidthScopes.For(PlanDoc, Repo + Path.DirectorySeparatorChar, groupIsRepo: true, new WidthSource(Docs));

        Assert.Equal(Repo, scopes.ClearAllRoot);
    }

    [Fact]
    public void Clear_all_target_of_a_viewer_is_the_documents_folder()
    {
        var scopes = WidthScopes.For(Notes, Downloads, groupIsRepo: false, WidthSource.AppDefault);

        Assert.Equal(Downloads, scopes.ClearAllRoot);
        Assert.Equal("Downloads", scopes.ClearAllName);
    }

    [Fact]
    public void Clear_all_name_is_the_group_roots_name_without_the_repo_mark_spelled_as_in_the_document()
    {
        Assert.SkipWhen(AppPaths.PathComparison == StringComparison.Ordinal, "This platform's paths are case-sensitive.");
        string document = Path.Combine(Repo.ToUpperInvariant(), "docs", "plans", "v1-build.md");

        var scopes = WidthScopes.For(document, Repo, groupIsRepo: true, WidthSource.AppDefault);

        Assert.Equal("YAMV", scopes.ClearAllName);
    }

    [Fact]
    public void Paths_that_differ_only_in_case_are_the_same_folder_where_the_platform_ignores_case()
    {
        Assert.SkipWhen(AppPaths.PathComparison == StringComparison.Ordinal, "This platform's paths are case-sensitive.");
        string document = Path.Combine(Repo.ToUpperInvariant(), "docs", "plans", "v1-build.md");

        var scopes = WidthScopes.For(document, Repo, groupIsRepo: true, new WidthSource(Docs.ToUpperInvariant()));
        WidthScopes reselected = scopes.Select(scopes.Segments[1], new WidthSource(Docs));

        Assert.Equal(["plans", "docs", "YAMV (repo)", "All documents"], Labels(scopes));
        Assert.Equal(1, SelectedIndex(scopes));
        Assert.Equal(Repo, scopes.ClearAllRoot);
        Assert.Null(reselected.InheritedFrom);
        Assert.True(reselected.CanReset);
        Assert.True(scopes.Contains(new WidthScope("x", Docs, IsSelected: false)));
    }

    [Fact]
    public void Caption_names_the_group_root_by_its_folder_name()
    {
        var opened = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, new WidthSource(Repo));

        WidthScopes scopes = opened.Select(opened.Segments[0], new WidthSource(Repo));

        Assert.Equal("yamv", scopes.InheritedFrom);
    }

    [Fact]
    public void Document_on_a_drive_root_has_the_root_and_all_documents()
    {
        string root = P();
        string document = Path.Combine(root, "a.md");

        var scopes = WidthScopes.For(document, root, groupIsRepo: false, WidthSource.AppDefault);

        Assert.Equal([root, "All documents"], Labels(scopes));
        Assert.Equal([root, null], Folders(scopes));
        Assert.Equal(root, scopes.ClearAllRoot);
    }

    [Fact]
    public void Source_at_the_drive_root_above_the_group_root_is_named_by_its_path()
    {
        string root = P();

        var scopes = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, new WidthSource(root));

        Assert.Equal(["docs/plans", "yamv (repo)", root, "All documents"], Labels(scopes));
        Assert.Equal(2, SelectedIndex(scopes));
    }

    [Fact]
    public void Contains_only_the_segments_shown()
    {
        var scopes = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, WidthSource.AppDefault);

        Assert.True(scopes.Contains(new WidthScope("any label", Plans, IsSelected: false)));
        Assert.True(scopes.Contains(new WidthScope("All documents", Folder: null, IsSelected: false)));
        Assert.False(scopes.Contains(new WidthScope("docs", Docs, IsSelected: false)));
    }

    [Fact]
    public void A_width_on_the_selected_segment_reaches_the_document_unless_a_nearer_folder_holds_one()
    {
        var scopes = WidthScopes.For(PlanDoc, Repo, groupIsRepo: true, new WidthSource(Plans));
        WidthScopes root = scopes.Select(scopes.Segments[1], WidthSource.AppDefault);
        WidthScopes all = scopes.Select(scopes.Segments[2], WidthSource.AppDefault);

        Assert.True(scopes.Reaches(new WidthSource(Plans)));
        Assert.False(root.Reaches(new WidthSource(Plans)));
        Assert.False(all.Reaches(new WidthSource(Plans)));
        Assert.True(scopes.Reaches(new WidthSource(Docs)));
        Assert.True(root.Reaches(new WidthSource(Repo)));
        Assert.True(all.Reaches(WidthSource.AppDefault));
        Assert.True(root.Reaches(WidthSource.AppDefault));
    }

    [Fact]
    public void Group_root_below_the_documents_folder_is_refused() =>
        Assert.Throws<ArgumentException>(() => WidthScopes.For(Readme, Docs, groupIsRepo: true, WidthSource.AppDefault));

    [Fact]
    public void Source_below_the_documents_folder_is_refused() =>
        Assert.Throws<ArgumentException>(() => WidthScopes.For(Readme, Repo, groupIsRepo: true, new WidthSource(Docs)));

    [Fact]
    public void Selecting_a_segment_of_other_scopes_is_refused()
    {
        var scopes = WidthScopes.For(Readme, Repo, groupIsRepo: true, WidthSource.AppDefault);

        Assert.Throws<ArgumentException>(() => scopes.Select(new WidthScope("docs", Docs, IsSelected: false), WidthSource.AppDefault));
    }

    private static string P(params string[] parts) => Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(parts));

    private static string[] Labels(WidthScopes scopes) => [.. scopes.Segments.Select(segment => segment.Label)];

    private static string?[] Folders(WidthScopes scopes) => [.. scopes.Segments.Select(segment => segment.Folder)];

    private static int SelectedIndex(WidthScopes scopes)
    {
        int index = Assert.Single(Enumerable.Range(0, scopes.Segments.Count), i => scopes.Segments[i].IsSelected);
        Assert.Same(scopes.Segments[index], scopes.Selected);
        return index;
    }
}
