using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Session;

namespace Yamv.App.Tests.ViewModels;

public sealed class DocumentWindowViewModelTests
{
    private static readonly TimeSpan GateBackstop = TimeSpan.FromSeconds(30);
    private static readonly GroupingOptions Plain = new(null, StringComparison.OrdinalIgnoreCase);
    private static readonly string Repo = P("dev", "yamv");
    private static readonly string RepoDocA = Path.Combine(Repo, "docs", "a.md");
    private static readonly string RepoDocB = Path.Combine(Repo, "docs", "b.md");
    private static readonly string RepoDocC = Path.Combine(Repo, "docs", "c.md");
    private static readonly string RepoRootDoc = Path.Combine(Repo, "root.md");
    private static readonly string Alpha = P("dev", "alpha");
    private static readonly string AlphaDoc = Path.Combine(Alpha, "x.md");

    [Fact]
    public Task Open_selects_first_added_and_shows_it_with_group_root() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();

            fixture.ViewModel.Open([RepoDocA, RepoRootDoc]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(RepoDocA, fixture.ViewModel.SelectedPath);
            Assert.Equal("a.md — YAMV", fixture.ViewModel.WindowTitle);
            ShownDocument shown = Assert.Single(fixture.Surface.Shown);
            Assert.Equal(RepoDocA, shown.DocumentPath);
            Assert.Equal(Repo, shown.GroupRoot);
            Assert.Equal(0, shown.Scroll);
            Assert.Null(shown.Fragment);
        });

    [Fact]
    public Task Stale_render_is_dropped_when_selection_moves_before_it_finishes() =>
        Headless.RunAsync(async () =>
        {
            using var gate = new ManualResetEventSlim();
            using var fixture = new Fixture();
            fixture.Reader = path =>
            {
                if (SamePath(path, RepoDocA))
                {
                    WaitFor(gate);
                }

                return fixture.Files[path];
            };

            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocB);
            gate.Set();
            await fixture.ViewModel.WhenRendersSettled();

            ShownDocument shown = Assert.Single(fixture.Surface.Shown);
            Assert.Equal(RepoDocB, shown.DocumentPath);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level == "error");
        });

    [Fact]
    public Task Older_render_of_same_path_does_not_overwrite_newer() =>
        Headless.RunAsync(async () =>
        {
            using var gate = new ManualResetEventSlim();
            using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            using var firstEntered = new ManualResetEventSlim();
            int reads = 0;
            fixture.Reader = _ =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    firstEntered.Set();
                    WaitFor(gate);
                    return "# First reload";
                }

                return "# Second reload";
            };
            fixture.Surface.OnShown = shown =>
            {
                if (shown.Html.Contains("Second reload", StringComparison.Ordinal))
                {
                    gate.Set();
                }
            };

            fixture.ViewModel.ReloadCommand.Execute(null);
            WaitFor(firstEntered);
            fixture.ViewModel.ReloadCommand.Execute(null);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(2, fixture.Surface.Shown.Count);
            Assert.Contains("Second reload", fixture.Surface.Shown[^1].Html, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level == "error");
        });

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    public Task Read_error_logs_warning_and_leaves_surface_unchanged(string failure) =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.Reader = path =>
                failure == "io" ? throw new IOException($"{path} is locked.") : throw new UnauthorizedAccessException($"{path} is not yours.");

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Empty(fixture.Surface.Shown);
            Assert.False(fixture.ViewModel.SelectedIsMissing);
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "warning" && entry.Message.Contains(RepoDocA, StringComparison.Ordinal));
        });

    [Fact]
    public Task Read_failure_for_deleted_file_marks_missing_without_warning() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.Reader = path =>
            {
                fixture.Absent.Add(path);
                throw new FileNotFoundException("Gone.", path);
            };

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.SelectedIsMissing);
            Assert.Empty(fixture.Surface.Shown);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level == "warning");
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Render_fault_of_unexpected_type_is_logged_as_error(bool returnsNull) =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.Reader = _ => returnsNull ? null! : throw new InvalidOperationException("Unexpected.");

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Empty(fixture.Surface.Shown);
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "error" && entry.Message.Contains(RepoDocA, StringComparison.Ordinal));
        });

    [Fact]
    public Task Selecting_missing_document_sets_SelectedIsMissing_and_does_not_show() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.Absent.Add(RepoDocB);
            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();

            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocB);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.SelectedIsMissing);
            Assert.Equal(RepoDocB, fixture.ViewModel.SelectedPath);
            ShownDocument shown = Assert.Single(fixture.Surface.Shown);
            Assert.Equal(RepoDocA, shown.DocumentPath);
        });

    [Fact]
    public Task Missing_document_that_reappears_is_shown_after_two_seconds() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.Absent.Add(RepoDocA);
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.ViewModel.SelectedIsMissing);

            fixture.Absent.Remove(RepoDocA);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(1900));
            await fixture.ViewModel.WhenRendersSettled();
            Assert.Empty(fixture.Surface.Shown);
            Assert.True(fixture.ViewModel.SelectedIsMissing);

            fixture.Time.Advance(TimeSpan.FromMilliseconds(100));
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(fixture.ViewModel.SelectedIsMissing);
            ShownDocument shown = Assert.Single(fixture.Surface.Shown);
            Assert.Equal(RepoDocA, shown.DocumentPath);
        });

    [Fact]
    public Task Reload_shows_missing_document_that_reappeared() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.Absent.Add(RepoDocA);
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Absent.Remove(RepoDocA);

            fixture.ViewModel.ReloadCommand.Execute(null);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(fixture.ViewModel.SelectedIsMissing);
            ShownDocument shown = Assert.Single(fixture.Surface.Shown);
            Assert.Equal(RepoDocA, shown.DocumentPath);
        });

    [Fact]
    public Task Dispose_stops_missing_recheck() =>
        Headless.Run(() =>
        {
            var fixture = new Fixture();
            fixture.Absent.Add(RepoDocA);
            fixture.ViewModel.Open([RepoDocA]);
            int probesBefore = fixture.ExistsCalls;

            fixture.ViewModel.Dispose();
            fixture.Time.Advance(TimeSpan.FromSeconds(5));

            Assert.Equal(probesBefore, fixture.ExistsCalls);
        });

    [Fact]
    public Task Close_selected_in_viewer_with_one_document_raises_CloseRequested() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture(persist: false);
            fixture.ViewModel.Open([RepoDocA]);
            int closeRequests = 0;
            fixture.ViewModel.CloseRequested += (_, _) => closeRequests++;

            fixture.ViewModel.CloseSelectedCommand.Execute(null);

            Assert.Equal(1, closeRequests);
        });

    [Fact]
    public Task Close_selected_in_main_with_one_document_leaves_empty_state() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            int closeRequests = 0;
            fixture.ViewModel.CloseRequested += (_, _) => closeRequests++;

            fixture.ViewModel.CloseSelectedCommand.Execute(null);

            Assert.True(fixture.ViewModel.IsEmpty);
            Assert.Null(fixture.ViewModel.SelectedPath);
            Assert.Equal("YAMV", fixture.ViewModel.WindowTitle);
            Assert.Equal(0, closeRequests);
        });

    [Fact]
    public Task Restore_collapses_groups_after_selection() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            var session = new SessionState
            {
                OpenFiles = [AlphaDoc, RepoDocA],
                SelectedFile = RepoDocA,
                CollapsedGroups = [Alpha],
            };

            fixture.ViewModel.Restore(session);

            Assert.Equal(RepoDocA, fixture.ViewModel.SelectedPath);
            Assert.True(GroupRow(fixture.ViewModel, Repo).IsExpanded);
            Assert.False(GroupRow(fixture.ViewModel, Alpha).IsExpanded);
            Assert.Equal([Alpha], fixture.State.CollapsedGroups);
        });

    [Fact]
    public Task Restore_keeps_selected_documents_group_collapsed_when_listed() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            var session = new SessionState
            {
                OpenFiles = [RepoDocA, AlphaDoc],
                SelectedFile = RepoDocA,
                CollapsedGroups = [Repo],
            };

            fixture.ViewModel.Restore(session);

            Assert.Equal(RepoDocA, fixture.ViewModel.SelectedPath);
            Assert.False(GroupRow(fixture.ViewModel, Repo).IsExpanded);
        });

    [Fact]
    public Task Restore_keeps_scroll_positions_in_first_snapshot() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            SessionState? first = null;
            fixture.ViewModel.SessionDirty += (_, _) => first ??= fixture.ViewModel.BuildSession(null, 280, "studio", "system");
            var session = new SessionState
            {
                OpenFiles = [RepoDocA],
                SelectedFile = RepoDocA,
                ScrollPositions = new Dictionary<string, double> { [RepoDocA] = 0.6 },
            };

            fixture.ViewModel.Restore(session);

            Assert.NotNull(first);
            Assert.Equal(0.6, first.ScrollPositions[RepoDocA]);
        });

    [Fact]
    public Task Restore_selects_first_present_document_when_selected_file_is_absent() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Absent.Add(RepoDocA);
            var session = new SessionState { OpenFiles = [RepoDocA, RepoDocB], SelectedFile = AlphaDoc };

            fixture.ViewModel.Restore(session);

            Assert.Equal(RepoDocB, fixture.ViewModel.SelectedPath);
            Assert.False(fixture.ViewModel.SelectedIsMissing);
        });

    [Fact]
    public Task Collapsing_a_group_survives_a_tree_rebuild() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, AlphaDoc]);

            GroupRow(fixture.ViewModel, Alpha).IsExpanded = false;
            fixture.ViewModel.Open([RepoRootDoc]);

            Assert.False(GroupRow(fixture.ViewModel, Alpha).IsExpanded);
            Assert.True(GroupRow(fixture.ViewModel, Repo).IsExpanded);
            Assert.Equal([Alpha], fixture.State.CollapsedGroups);
        });

    [Fact]
    public Task Selecting_group_row_restores_document_selection() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);

            fixture.ViewModel.SelectedItem = GroupRow(fixture.ViewModel, Repo);
            Dispatcher.UIThread.RunJobs();

            DocumentItemViewModel row = Assert.IsType<DocumentItemViewModel>(fixture.ViewModel.SelectedItem);
            Assert.Equal(RepoDocA, row.Path);
            Assert.Equal(RepoDocA, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Writing_null_SelectedItem_restores_document_selection() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);

            fixture.ViewModel.SelectedItem = null;
            Dispatcher.UIThread.RunJobs();

            DocumentItemViewModel row = Assert.IsType<DocumentItemViewModel>(fixture.ViewModel.SelectedItem);
            Assert.Equal(RepoDocA, row.Path);
            Assert.Equal(RepoDocA, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Restore_skips_invalid_saved_path_with_warning() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            string invalid = Path.Combine(Repo, "bad\0name.md");
            var session = new SessionState { OpenFiles = [invalid, RepoDocA], SelectedFile = invalid };

            fixture.ViewModel.Restore(session);

            Assert.Equal([RepoDocA], fixture.State.Documents);
            Assert.Equal(RepoDocA, fixture.ViewModel.SelectedPath);
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "warning" && entry.Message.Contains("bad", StringComparison.Ordinal));
        });

    [Fact]
    public Task Scroll_change_updates_snapshot_and_raises_SessionDirty() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            List<bool> dirty = [];
            fixture.ViewModel.SessionDirty += (_, args) => dirty.Add(args.ScrollOnly);

            fixture.Surface.RaiseScroll(0.4);

            Assert.Equal([true], dirty);
            SessionState session = fixture.ViewModel.BuildSession(null, 280, "studio", "system");
            Assert.Equal(0.4, session.ScrollPositions[RepoDocA]);
        });

    [Fact]
    public Task Scroll_from_previous_document_is_not_saved_under_new_selection() =>
        Headless.RunAsync(async () =>
        {
            using var gate = new ManualResetEventSlim();
            using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Reader = path =>
            {
                WaitFor(gate);
                return fixture.Files[path];
            };

            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocB);
            fixture.Surface.RaiseScroll(0.7);
            SessionState session = fixture.ViewModel.BuildSession(null, 280, "studio", "system");
            gate.Set();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(session.ScrollPositions.ContainsKey(RepoDocB));
            Assert.False(session.ScrollPositions.ContainsKey(RepoDocA));
        });

    [Fact]
    public Task SessionDirty_is_raised_for_selection_collapse_sidebar_and_documents_not_scroll_only() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            List<bool> dirty = [];
            fixture.ViewModel.SessionDirty += (_, args) => dirty.Add(args.ScrollOnly);

            fixture.ViewModel.Open([RepoDocA, AlphaDoc]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, AlphaDoc);
            await fixture.ViewModel.WhenRendersSettled();
            GroupRow(fixture.ViewModel, Repo).IsExpanded = false;
            fixture.ViewModel.ToggleSidebarCommand.Execute(null);
            fixture.Surface.RaiseScroll(0.2);
            fixture.Absent.Add(RepoDocA);
            fixture.Time.Advance(TimeSpan.FromSeconds(2));

            Assert.Equal([false, false, false, false, true], dirty);
        });

    [Fact]
    public Task Replace_moves_scroll_position_to_new_path() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.Files[RepoDocC] = "# C";
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Surface.RaiseScroll(0.5);

            fixture.ViewModel.Replace(RepoDocA, RepoDocC);
            await fixture.ViewModel.WhenRendersSettled();

            SessionState session = fixture.ViewModel.BuildSession(null, 280, "studio", "system");
            Assert.Equal(0.5, session.ScrollPositions[RepoDocC]);
            Assert.False(session.ScrollPositions.ContainsKey(RepoDocA));
            Assert.Equal(0.5, fixture.Surface.Shown[^1].Scroll);
        });

    [Fact]
    public Task Reload_rereads_and_keeps_scroll() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Surface.RaiseScroll(0.3);
            fixture.Files[RepoDocA] = "# Changed heading";

            fixture.ViewModel.ReloadCommand.Execute(null);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(2, fixture.Surface.Shown.Count);
            ShownDocument reloaded = fixture.Surface.Shown[1];
            Assert.Equal(RepoDocA, reloaded.DocumentPath);
            Assert.Equal(0.3, reloaded.Scroll);
            Assert.Contains("Changed heading", reloaded.Html, StringComparison.Ordinal);
        });

    [Fact]
    public Task BuildSession_includes_sidebar_hidden_and_collapsed_groups() =>
        Headless.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, AlphaDoc]);
            GroupRow(fixture.ViewModel, Alpha).IsExpanded = false;
            fixture.ViewModel.ToggleSidebarCommand.Execute(null);

            SessionState session = fixture.ViewModel.BuildSession(new WindowBounds(10, 20, 800, 600, false), 300, "ink", "dark");

            Assert.True(session.SidebarHidden);
            Assert.Equal([Alpha], session.CollapsedGroups);
            Assert.Equal([RepoDocA, AlphaDoc], session.OpenFiles);
            Assert.Equal(RepoDocA, session.SelectedFile);
            Assert.Equal(300, session.SidebarWidth);
            Assert.Equal("ink", session.ThemeId);
            Assert.Equal("dark", session.Appearance);
        });

    private static string P(params string[] parts) => Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(parts));

    private static bool SamePath(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    /// <summary>Waits for a test's gate, failing the read (and so the test) if it is never opened.</summary>
    private static void WaitFor(ManualResetEventSlim gate)
    {
        if (!gate.Wait(GateBackstop, TestContext.Current.CancellationToken))
        {
            throw new TimeoutException($"The test gate was not opened within {GateBackstop.TotalSeconds} s.");
        }
    }

    private static string? FindRepo(string path) =>
        path.StartsWith(Repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Repo
        : path.StartsWith(Alpha + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Alpha
        : null;

    private static IEnumerable<TreeItemViewModel> Flatten(IEnumerable<TreeItemViewModel> items) =>
        items.SelectMany(item =>
            item switch
            {
                GroupItemViewModel group => Flatten(group.Children).Prepend(item),
                FolderItemViewModel folder => Flatten(folder.Children).Prepend(item),
                _ => [item],
            }
        );

    private static DocumentItemViewModel DocumentRow(DocumentWindowViewModel viewModel, string path) =>
        Flatten(viewModel.Items).OfType<DocumentItemViewModel>().Single(row => SamePath(row.Path, path));

    private static GroupItemViewModel GroupRow(DocumentWindowViewModel viewModel, string path) =>
        viewModel.Items.OfType<GroupItemViewModel>().Single(row => SamePath(row.Path, path));

    /// <summary>A view model over fake files, a fake surface and a fake clock.</summary>
    private sealed class Fixture : IDisposable
    {
        private int _existsCalls;

        public Fixture(bool persist = true)
        {
            Reader = path => Files.TryGetValue(path, out string? text) ? text : throw new FileNotFoundException("No such fake file.", path);
            State = new DocumentWindowState(persist, FindRepo, Exists, Plain);
            ViewModel = new DocumentWindowViewModel(State, Surface, path => Reader(path), Time, Log);
        }

        public HashSet<string> Absent { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Files { get; } =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [RepoDocA] = "# A",
                [RepoDocB] = "# B",
                [RepoRootDoc] = "# Root",
                [AlphaDoc] = "# X",
            };

        public Func<string, string> Reader { get; set; }

        /// <summary>How many times the state asked whether a document is on disk.</summary>
        public int ExistsCalls => Volatile.Read(ref _existsCalls);

        public FakeDocumentSurface Surface { get; } = new();

        public RecordingAppLog Log { get; } = new();

        public FakeTimeProvider Time { get; } = new();

        public DocumentWindowState State { get; }

        public DocumentWindowViewModel ViewModel { get; }

        public void Dispose() => ViewModel.Dispose();

        private bool Exists(string path)
        {
            Interlocked.Increment(ref _existsCalls);
            return !Absent.Contains(path);
        }
    }
}
