using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Session;
using Yamv.Core.Widths;

namespace Yamv.App.Tests.ViewModels;

public sealed class DocumentWindowViewModelTests
{
    private const double WidePane = 1600;
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
            await using var fixture = new Fixture();

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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
    [InlineData("io", ReadFailure.InUseByAnotherProgram)]
    [InlineData("access", ReadFailure.AccessDenied)]
    public Task Read_error_logs_warning_and_leaves_surface_unchanged(string failure, string reason) =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Reader = path =>
                failure == "io"
                    ? throw new IOException($"{path} is locked.", unchecked((int)0x80070020))
                    : throw new UnauthorizedAccessException($"{path} is not yours.");

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Empty(fixture.Surface.Shown);
            Assert.False(fixture.ViewModel.SelectedIsMissing);
            Assert.True(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Equal(reason, fixture.ViewModel.UnreadableReason);
            Assert.True(DocumentRow(fixture.ViewModel, RepoDocA).IsUnreadable);
            Assert.Single(fixture.Log.Entries, entry => entry.Level == "warning");
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "warning" && entry.Message.Contains(RepoDocA, StringComparison.Ordinal));
        });

    [Fact]
    public Task Unreadable_document_retried_by_the_timer_logs_no_second_warning() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            int reads = 0;
            fixture.Reader = _ =>
            {
                Interlocked.Increment(ref reads);
                throw new IOException("The file is locked.", unchecked((int)0x80070020));
            };

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.ViewModel.SelectedIsUnreadable);

            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(3, reads);
            Assert.True(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Single(fixture.Log.Entries, entry => entry.Level == "warning");
        });

    [Fact]
    public Task Retry_ticks_do_not_restart_a_read_that_is_still_running() =>
        Headless.RunAsync(async () =>
        {
            using var gate = new ManualResetEventSlim();
            using var secondRead = new ManualResetEventSlim();
            await using var fixture = new Fixture();
            int reads = 0;
            fixture.Reader = _ =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    throw new IOException("The file is locked.", unchecked((int)0x80070020));
                }

                secondRead.Set();
                WaitFor(gate);
                return "# Read at last";
            };

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.ViewModel.SelectedIsUnreadable);

            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            WaitFor(secondRead);
            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            gate.Set();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(2, reads);
            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            ShownDocument shown = Assert.Single(fixture.Surface.Shown);
            Assert.Equal(RepoDocA, shown.DocumentPath);
        });

    [Fact]
    public Task Slow_failure_of_an_older_render_is_dropped() =>
        Headless.RunAsync(async () =>
        {
            using var gate = new ManualResetEventSlim();
            using var firstRead = new ManualResetEventSlim();
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            int reads = 0;
            fixture.Reader = _ =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    firstRead.Set();
                    WaitFor(gate);
                    throw new IOException("The file is locked.", unchecked((int)0x80070020));
                }

                return "# Read";
            };
            fixture.Surface.OnShown = _ => gate.Set();

            fixture.ViewModel.ReloadCommand.Execute(null);
            WaitFor(firstRead);
            fixture.ViewModel.ReloadCommand.Execute(null);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            Assert.False(DocumentRow(fixture.ViewModel, RepoDocA).IsUnreadable);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level == "warning");
            Assert.Equal(RepoDocA, fixture.Surface.Shown[^1].DocumentPath);
        });

    [Fact]
    public Task Unreadable_document_that_goes_missing_becomes_missing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Reader = _ => throw new UnauthorizedAccessException("The file is not yours.");

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.ViewModel.SelectedIsUnreadable);

            fixture.Absent.Add(RepoDocA);
            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.SelectedIsMissing);
            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            Assert.False(DocumentRow(fixture.ViewModel, RepoDocA).IsUnreadable);
        });

    [Fact]
    public Task Closing_another_document_keeps_the_unreadable_row_and_pane() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Reader = path => path == RepoDocA ? throw new UnauthorizedAccessException("The file is not yours.") : fixture.Files[path];

            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.ViewModel.SelectedIsUnreadable);

            DocumentRow(fixture.ViewModel, RepoDocB).CloseCommand.Execute(null);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Equal(ReadFailure.AccessDenied, fixture.ViewModel.UnreadableReason);
            Assert.True(DocumentRow(fixture.ViewModel, RepoDocA).IsUnreadable);
        });

    [Fact]
    public Task Unreadable_document_read_on_a_later_tick_is_shown() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            bool locked = true;
            fixture.Reader = path => locked ? throw new IOException("The file is locked.", unchecked((int)0x80070020)) : fixture.Files[path];

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.ViewModel.SelectedIsUnreadable);

            locked = false;
            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Equal("", fixture.ViewModel.UnreadableReason);
            Assert.False(DocumentRow(fixture.ViewModel, RepoDocA).IsUnreadable);
            ShownDocument shown = Assert.Single(fixture.Surface.Shown);
            Assert.Equal(RepoDocA, shown.DocumentPath);
        });

    [Fact]
    public Task Reload_after_the_read_succeeds_clears_unreadable_and_shows_the_page() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            bool locked = true;
            fixture.Reader = path => locked ? throw new UnauthorizedAccessException("The file is not yours.") : fixture.Files[path];

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.ViewModel.SelectedIsUnreadable);

            locked = false;
            fixture.ViewModel.ReloadCommand.Execute(null);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Equal("", fixture.ViewModel.UnreadableReason);
            Assert.False(DocumentRow(fixture.ViewModel, RepoDocA).IsUnreadable);
            ShownDocument shown = Assert.Single(fixture.Surface.Shown);
            Assert.Equal(RepoDocA, shown.DocumentPath);
        });

    [Fact]
    public Task Selecting_another_document_clears_the_unreadable_state_and_row_flag() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Reader = path => path == RepoDocA ? throw new UnauthorizedAccessException("The file is not yours.") : fixture.Files[path];

            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.ViewModel.SelectedIsUnreadable);

            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocB);
            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Equal("", fixture.ViewModel.UnreadableReason);
            Assert.False(DocumentRow(fixture.ViewModel, RepoDocA).IsUnreadable);
            Assert.Equal(RepoDocB, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Remove_from_list_closes_the_unreadable_document_and_selects_another() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Reader = path => path == RepoDocA ? throw new UnauthorizedAccessException("The file is not yours.") : fixture.Files[path];

            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();

            fixture.ViewModel.RemoveMissingCommand.Execute(RepoDocA);
            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.DoesNotContain(RepoDocA, fixture.State.Documents);
            Assert.Equal(RepoDocB, fixture.ViewModel.SelectedPath);
            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
        });

    [Fact]
    public Task Generic_read_failure_shows_the_generic_reason_and_logs_the_message() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Reader = _ => throw new IOException("The disk went away.", unchecked((int)0x80070001));

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Equal(ReadFailure.Unknown, fixture.ViewModel.UnreadableReason);
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "warning" && entry.Exception?.Message == "The disk went away.");
        });

    [Fact]
    public Task Read_failure_for_deleted_file_marks_missing_without_warning() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Absent.Add(RepoDocA);
            fixture.ViewModel.Open([RepoDocA]);
            int probesBefore = fixture.ExistsCalls;

            fixture.ViewModel.Dispose();
            fixture.Time.Advance(TimeSpan.FromSeconds(5));

            Assert.Equal(probesBefore, fixture.ExistsCalls);
        });

    [Fact]
    public Task Close_selected_in_viewer_with_one_document_raises_CloseRequested() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture(persist: false);
            fixture.ViewModel.Open([RepoDocA]);
            int closeRequests = 0;
            fixture.ViewModel.CloseRequested += (_, _) => closeRequests++;

            fixture.ViewModel.CloseSelectedCommand.Execute(null);

            Assert.Equal(1, closeRequests);
        });

    [Fact]
    public Task Close_selected_in_main_with_one_document_leaves_empty_state() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
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
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
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
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
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
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            SessionState? first = null;
            fixture.ViewModel.SessionDirty += (_, _) => first ??= fixture.ViewModel.BuildSession(null, 280);
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
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Absent.Add(RepoDocA);
            var session = new SessionState { OpenFiles = [RepoDocA, RepoDocB], SelectedFile = AlphaDoc };

            fixture.ViewModel.Restore(session);

            Assert.Equal(RepoDocB, fixture.ViewModel.SelectedPath);
            Assert.False(fixture.ViewModel.SelectedIsMissing);
        });

    [Fact]
    public Task Collapsing_a_group_survives_a_tree_rebuild() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, AlphaDoc]);

            GroupRow(fixture.ViewModel, Alpha).IsExpanded = false;
            fixture.ViewModel.Open([RepoRootDoc]);

            Assert.False(GroupRow(fixture.ViewModel, Alpha).IsExpanded);
            Assert.True(GroupRow(fixture.ViewModel, Repo).IsExpanded);
            Assert.Equal([Alpha], fixture.State.CollapsedGroups);
        });

    [Fact]
    public Task Selecting_group_row_restores_document_selection() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);

            fixture.ViewModel.SelectedItem = GroupRow(fixture.ViewModel, Repo);
            Dispatcher.UIThread.RunJobs();

            DocumentItemViewModel row = Assert.IsType<DocumentItemViewModel>(fixture.ViewModel.SelectedItem);
            Assert.Equal(RepoDocA, row.Path);
            Assert.Equal(RepoDocA, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Writing_null_SelectedItem_restores_document_selection() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);

            fixture.ViewModel.SelectedItem = null;
            Dispatcher.UIThread.RunJobs();

            DocumentItemViewModel row = Assert.IsType<DocumentItemViewModel>(fixture.ViewModel.SelectedItem);
            Assert.Equal(RepoDocA, row.Path);
            Assert.Equal(RepoDocA, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Restore_skips_invalid_saved_path_with_warning() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            List<bool> dirty = [];
            fixture.ViewModel.SessionDirty += (_, args) => dirty.Add(args.ScrollOnly);

            fixture.Surface.RaiseScroll(0.4, null);

            Assert.Equal([true], dirty);
            SessionState session = fixture.ViewModel.BuildSession(null, 280);
            Assert.Equal(0.4, session.ScrollPositions[RepoDocA]);
        });

    [Fact]
    public Task Scroll_from_previous_document_is_not_saved_under_new_selection() =>
        Headless.RunAsync(async () =>
        {
            using var gate = new ManualResetEventSlim();
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Reader = path =>
            {
                WaitFor(gate);
                return fixture.Files[path];
            };

            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocB);
            fixture.Surface.RaiseScroll(0.7, null);
            SessionState session = fixture.ViewModel.BuildSession(null, 280);
            gate.Set();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(session.ScrollPositions.ContainsKey(RepoDocB));
            Assert.False(session.ScrollPositions.ContainsKey(RepoDocA));
        });

    [Fact]
    public Task SessionDirty_is_raised_for_selection_collapse_sidebar_and_documents_not_scroll_only() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            List<bool> dirty = [];
            fixture.ViewModel.SessionDirty += (_, args) => dirty.Add(args.ScrollOnly);

            fixture.ViewModel.Open([RepoDocA, AlphaDoc]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, AlphaDoc);
            await fixture.ViewModel.WhenRendersSettled();
            GroupRow(fixture.ViewModel, Repo).IsExpanded = false;
            fixture.ViewModel.ToggleSidebarCommand.Execute(null);
            fixture.Surface.RaiseScroll(0.2, null);
            fixture.Absent.Add(RepoDocA);
            fixture.Time.Advance(TimeSpan.FromSeconds(2));

            Assert.Equal([false, false, false, false, true], dirty);
        });

    [Fact]
    public Task Replace_moves_scroll_position_to_new_path() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Files[RepoDocC] = "# C";
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Surface.RaiseScroll(0.5, null);

            fixture.ViewModel.Replace(RepoDocA, RepoDocC);
            await fixture.ViewModel.WhenRendersSettled();

            SessionState session = fixture.ViewModel.BuildSession(null, 280);
            Assert.Equal(0.5, session.ScrollPositions[RepoDocC]);
            Assert.False(session.ScrollPositions.ContainsKey(RepoDocA));
            Assert.Equal(0.5, fixture.Surface.Shown[^1].Scroll);
        });

    [Fact]
    public Task Reload_rereads_and_keeps_scroll() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Surface.RaiseScroll(0.3, null);
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
    public Task A_reload_sends_the_anchor_not_the_stale_fraction() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Surface.RaiseScroll(0.3, new ScrollAnchor(14, 0.35));
            fixture.Files[RepoDocA] = "# Twenty lines longer";

            fixture.ViewModel.ReloadCommand.Execute(null);
            await fixture.ViewModel.WhenRendersSettled();

            ShownDocument reloaded = fixture.Surface.Shown[^1];
            Assert.Equal((RepoDocA, new ScrollAnchor(14, 0.35)), (reloaded.DocumentPath, reloaded.Anchor));
        });

    [Fact]
    public Task Switching_back_to_a_document_sends_its_stored_anchor() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Surface.RaiseScroll(0.4, new ScrollAnchor(9, 0.5));
            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocB);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Surface.RaiseScroll(0.1, new ScrollAnchor(2, 0));
            fixture.Files[RepoDocA] = "# A changed while B was shown";

            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocA);
            await fixture.ViewModel.WhenRendersSettled();

            ShownDocument shown = fixture.Surface.Shown[^1];
            Assert.Equal((RepoDocA, new ScrollAnchor(9, 0.5), 0.4), (shown.DocumentPath, shown.Anchor, shown.Scroll));
        });

    [Fact]
    public Task A_scroll_without_an_anchor_forgets_the_documents_anchor() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Surface.RaiseScroll(0.3, new ScrollAnchor(14, 0.2));

            fixture.Surface.RaiseScroll(0.6, null);
            fixture.ViewModel.ReloadCommand.Execute(null);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal((0.6, null), (fixture.Surface.Shown[^1].Scroll, fixture.Surface.Shown[^1].Anchor));
            Assert.False(fixture.ViewModel.BuildSession(null, 280).ScrollAnchors.ContainsKey(RepoDocA));
        });

    [Fact]
    public Task Replace_moves_the_scroll_anchor_to_the_new_path() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Files[RepoDocC] = "# C";
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Surface.RaiseScroll(0.5, new ScrollAnchor(6, 0.25));

            fixture.ViewModel.Replace(RepoDocA, RepoDocC);
            await fixture.ViewModel.WhenRendersSettled();

            SessionState session = fixture.ViewModel.BuildSession(null, 280);
            Assert.Equal(new ScrollAnchor(6, 0.25), session.ScrollAnchors[RepoDocC]);
            Assert.False(session.ScrollAnchors.ContainsKey(RepoDocA));
            Assert.Equal((RepoDocC, new ScrollAnchor(6, 0.25)), (fixture.Surface.Shown[^1].DocumentPath, fixture.Surface.Shown[^1].Anchor));
        });

    [Fact]
    public Task Restoring_a_session_sends_each_documents_anchor() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            var session = new SessionState
            {
                OpenFiles = [RepoDocA, RepoDocB],
                SelectedFile = RepoDocA,
                ScrollPositions = new Dictionary<string, double> { [RepoDocA] = 0.2, [RepoDocB] = 0.6 },
                ScrollAnchors = new Dictionary<string, ScrollAnchor> { [RepoDocA] = new(7, 0.1), [RepoDocB] = new(11, 0.9) },
            };

            fixture.ViewModel.Restore(session);
            await fixture.ViewModel.WhenRendersSettled();
            ShownDocument first = fixture.Surface.Shown[^1];
            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocB);
            await fixture.ViewModel.WhenRendersSettled();
            ShownDocument second = fixture.Surface.Shown[^1];

            Assert.Equal((RepoDocA, new ScrollAnchor(7, 0.1)), (first.DocumentPath, first.Anchor));
            Assert.Equal((RepoDocB, new ScrollAnchor(11, 0.9)), (second.DocumentPath, second.Anchor));
            SessionState saved = fixture.ViewModel.BuildSession(null, 280);
            Assert.Equal(new ScrollAnchor(7, 0.1), saved.ScrollAnchors[RepoDocA]);
            Assert.Equal(new ScrollAnchor(11, 0.9), saved.ScrollAnchors[RepoDocB]);
        });

    [Fact]
    public Task An_in_use_reload_probes_only_its_own_path() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Reader = path => SamePath(path, RepoDocA) ? throw InUse(path) : fixture.Files[path];
            fixture.Absent.Add(RepoDocB);

            await fixture.ReportAsync(RepoDocA);

            Assert.False(DocumentRow(fixture.ViewModel, RepoDocB).IsMissing);
            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Single(fixture.Surface.Shown);
        });

    [Fact]
    public Task Opening_documents_sets_the_watched_paths() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            bool watchedBeforeRead = false;
            fixture.Reader = path =>
            {
                watchedBeforeRead = fixture.Changes.SetPathsCalls.Count > 0;
                return fixture.Files[path];
            };

            fixture.ViewModel.Open([RepoDocA, RepoRootDoc]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal<string>(fixture.State.Documents, Assert.Single(fixture.Changes.SetPathsCalls));
            Assert.Contains(RepoRootDoc, fixture.State.Documents);
            Assert.True(watchedBeforeRead, "the document was read before the window watched it");
        });

    [Fact]
    public Task Closing_and_replacing_a_document_updates_the_watched_paths() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Files[RepoDocC] = "# C";
            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();

            DocumentRow(fixture.ViewModel, RepoDocB).CloseCommand.Execute(null);
            fixture.ViewModel.Replace(RepoDocA, RepoDocC);
            await fixture.ViewModel.WhenRendersSettled();

            IReadOnlyList<IReadOnlyList<string>> calls = fixture.Changes.SetPathsCalls;
            Assert.Equal(3, calls.Count);
            Assert.Equal<string>([RepoDocA], calls[1]);
            Assert.Equal<string>([RepoDocC], calls[2]);
        });

    [Fact]
    public Task A_selection_change_does_not_call_SetPaths_again() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();

            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocB);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(RepoDocB, fixture.Surface.Shown[^1].DocumentPath);
            Assert.Single(fixture.Changes.SetPathsCalls);
        });

    [Fact]
    public Task A_change_to_the_selected_document_rereads_and_shows_it_again_at_the_same_scroll() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Surface.RaiseScroll(0.3, null);
            fixture.Files[RepoDocA] = "# Saved in an editor";

            await fixture.ReportAsync(RepoDocA);

            Assert.Equal(2, fixture.Surface.Shown.Count);
            ShownDocument reloaded = fixture.Surface.Shown[1];
            Assert.Equal(RepoDocA, reloaded.DocumentPath);
            Assert.Equal(0.3, reloaded.Scroll);
            Assert.Contains("Saved in an editor", reloaded.Html, StringComparison.Ordinal);
        });

    [Fact]
    public Task A_change_to_another_document_greys_its_row_without_a_render() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Absent.Add(RepoDocB);
            fixture.Files.Remove(RepoDocB);

            await fixture.ReportAsync(RepoDocB);

            Assert.True(DocumentRow(fixture.ViewModel, RepoDocB).IsMissing);
            Assert.Equal(RepoDocA, fixture.ViewModel.SelectedPath);
            Assert.False(fixture.ViewModel.SelectedIsMissing);
            Assert.Single(fixture.Surface.Shown);
        });

    [Fact]
    public Task A_change_to_a_document_that_is_not_open_is_ignored() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            int probes = fixture.ExistsCalls;
            fixture.Absent.Add(AlphaDoc);

            await fixture.ReportAsync(AlphaDoc);

            Assert.Equal(probes, fixture.ExistsCalls);
            Assert.DoesNotContain(AlphaDoc, fixture.State.Documents);
            Assert.Single(fixture.Surface.Shown);
        });

    [Fact]
    public Task A_burst_of_reports_renders_the_latest_content_once() =>
        Headless.RunAsync(async () =>
        {
            using var gate = new ManualResetEventSlim();
            using var entered = new SemaphoreSlim(0);
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            int reads = 0;
            fixture.Reader = path =>
            {
                string text = fixture.Files[path];
                Interlocked.Increment(ref reads);
                entered.Release();
                WaitFor(gate);
                return text;
            };

            foreach (string save in (string[])["# First save", "# Second save", "# Third save"])
            {
                fixture.Files[RepoDocA] = save;
                fixture.RaiseFromPool(RepoDocA);
                Dispatcher.UIThread.RunJobs();
                WaitFor(entered);
            }

            gate.Set();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(3, reads);
            Assert.Equal(2, fixture.Surface.Shown.Count);
            Assert.Contains("Third save", fixture.Surface.Shown[1].Html, StringComparison.Ordinal);
        });

    [Fact]
    public Task A_report_for_a_missing_selection_that_returned_renders_once() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Absent.Add(RepoDocA);
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.ViewModel.SelectedIsMissing);
            fixture.Absent.Remove(RepoDocA);

            await fixture.ReportAsync(RepoDocA);

            Assert.False(fixture.ViewModel.SelectedIsMissing);
            ShownDocument shown = Assert.Single(fixture.Surface.Shown);
            Assert.Equal(RepoDocA, shown.DocumentPath);
        });

    [Fact]
    public Task A_reload_with_unchanged_html_shows_nothing_new() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            int reads = 0;
            fixture.Reader = path =>
            {
                Interlocked.Increment(ref reads);
                return fixture.Files[path];
            };

            await fixture.ReportAsync(RepoDocA);

            Assert.Equal(1, reads);
            Assert.Single(fixture.Surface.Shown);
        });

    [Fact]
    public Task A_report_after_dispose_does_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Files[RepoDocA] = "# Saved after the window went";

            fixture.RaiseFromPool(RepoDocA);
            fixture.ViewModel.Dispose();
            Dispatcher.UIThread.RunJobs();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Single(fixture.Surface.Shown);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level == "error");
        });

    [Fact]
    public Task Dispose_disposes_the_watcher_and_unsubscribes() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Changes.HasSubscribers);

            fixture.ViewModel.Dispose();

            Assert.True(fixture.Changes.Disposed);
            Assert.False(fixture.Changes.HasSubscribers);
        });

    [Fact]
    public Task A_throwing_SetPaths_is_logged_and_the_window_keeps_working() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Changes.ThrowOnSetPaths = new InvalidOperationException("The watch was refused.");

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.ViewModel.Open([RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "error" && entry.Exception?.Message == "The watch was refused.");
            Assert.Equal(2, fixture.Changes.SetPathsCalls.Count);
            Assert.Equal([RepoDocA, RepoDocB], fixture.Surface.Shown.Select(shown => shown.DocumentPath));
        });

    [Fact]
    public Task A_delete_only_report_keeps_the_page_for_500_ms_then_shows_the_missing_pane() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Absent.Add(RepoDocA);
            fixture.Files.Remove(RepoDocA);

            await fixture.ReportAsync(RepoDocA);
            Assert.False(fixture.ViewModel.SelectedIsMissing);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(499));
            await fixture.ViewModel.WhenRendersSettled();
            Assert.False(fixture.ViewModel.SelectedIsMissing);

            fixture.Time.Advance(TimeSpan.FromMilliseconds(1));
            Dispatcher.UIThread.RunJobs();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.SelectedIsMissing);
            Assert.True(DocumentRow(fixture.ViewModel, RepoDocA).IsMissing);
            Assert.Single(fixture.Surface.Shown);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level is "warning" or "error");
        });

    [Fact]
    public Task A_delete_and_recreate_inside_500_ms_never_shows_the_missing_pane() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            bool everMissing = false;
            fixture.ViewModel.PropertyChanged += (_, _) => everMissing |= fixture.ViewModel.SelectedIsMissing;
            fixture.Absent.Add(RepoDocA);
            fixture.Files.Remove(RepoDocA);

            await fixture.ReportAsync(RepoDocA);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(200));
            fixture.Absent.Remove(RepoDocA);
            fixture.Files[RepoDocA] = "# Re-created by the editor";
            fixture.Time.Advance(TimeSpan.FromMilliseconds(300));
            Dispatcher.UIThread.RunJobs();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(everMissing, "the missing pane showed during the grace");
            Assert.False(fixture.ViewModel.SelectedIsMissing);
            Assert.Equal(2, fixture.Surface.Shown.Count);
            Assert.Contains("Re-created by the editor", fixture.Surface.Shown[1].Html, StringComparison.Ordinal);
        });

    [Fact]
    public Task The_2_s_tick_inside_the_grace_does_not_show_the_missing_pane() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Time.Advance(TimeSpan.FromMilliseconds(1800));
            fixture.Absent.Add(RepoDocA);
            fixture.Files.Remove(RepoDocA);

            await fixture.ReportAsync(RepoDocA);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(300));
            Dispatcher.UIThread.RunJobs();
            await fixture.ViewModel.WhenRendersSettled();
            Assert.False(fixture.ViewModel.SelectedIsMissing);

            fixture.Time.Advance(TimeSpan.FromMilliseconds(200));
            Dispatcher.UIThread.RunJobs();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.SelectedIsMissing);
            Assert.Single(fixture.Surface.Shown);
        });

    [Fact]
    public Task A_gone_reload_left_behind_by_a_selection_change_greys_its_row_at_once() =>
        Headless.RunAsync(async () =>
        {
            using var gate = new ManualResetEventSlim();
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Reader = path =>
            {
                if (SamePath(path, RepoDocA))
                {
                    WaitFor(gate);
                    if (fixture.Absent.Contains(path))
                    {
                        throw new FileNotFoundException("No such fake file.", path);
                    }
                }

                return fixture.Files[path];
            };
            fixture.RaiseFromPool(RepoDocA);
            Dispatcher.UIThread.RunJobs();

            fixture.Absent.Add(RepoDocA);
            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocB);
            gate.Set();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(DocumentRow(fixture.ViewModel, RepoDocA).IsMissing);
            Assert.Equal(RepoDocB, fixture.ViewModel.SelectedPath);
            Assert.False(fixture.ViewModel.SelectedIsMissing);
            Assert.Equal(RepoDocB, fixture.Surface.Shown[^1].DocumentPath);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level is "warning" or "error");
        });

    [Fact]
    public Task An_in_use_reload_keeps_the_old_page_and_retries_on_the_next_event() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            int reads = 0;
            bool locked = true;
            fixture.Reader = path =>
            {
                Interlocked.Increment(ref reads);
                return Volatile.Read(ref locked) ? throw InUse(path) : fixture.Files[path];
            };
            fixture.Files[RepoDocA] = "# Saved while locked";

            await fixture.ReportAsync(RepoDocA);
            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Equal("", fixture.ViewModel.UnreadableReason);
            Assert.Single(fixture.Surface.Shown);

            Volatile.Write(ref locked, false);
            await fixture.ReportAsync(RepoDocA);

            Assert.Equal(2, reads);
            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Equal(2, fixture.Surface.Shown.Count);
            Assert.Contains("Saved while locked", fixture.Surface.Shown[1].Html, StringComparison.Ordinal);
        });

    [Fact]
    public Task An_in_use_reload_retries_on_the_2_s_tick() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            bool locked = true;
            fixture.Reader = path => Volatile.Read(ref locked) ? throw InUse(path) : fixture.Files[path];
            fixture.Files[RepoDocA] = "# Saved while locked";
            await fixture.ReportAsync(RepoDocA);
            Assert.False(fixture.ViewModel.SelectedIsUnreadable);

            Volatile.Write(ref locked, false);
            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(2, fixture.Surface.Shown.Count);
            Assert.Contains("Saved while locked", fixture.Surface.Shown[1].Html, StringComparison.Ordinal);
            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
        });

    [Fact]
    public Task An_in_use_reload_shows_the_cant_read_pane_only_after_10_s_of_failures() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            int reads = 0;
            fixture.Reader = path =>
            {
                Interlocked.Increment(ref reads);
                throw InUse(path);
            };

            await fixture.ReportAsync(RepoDocA);
            for (int tick = 1; tick <= 4; tick++)
            {
                fixture.Time.Advance(TimeSpan.FromSeconds(2));
                await fixture.ViewModel.WhenRendersSettled();
            }

            fixture.Time.Advance(TimeSpan.FromMilliseconds(1900));
            await fixture.ViewModel.WhenRendersSettled();
            Assert.Equal(5, reads);
            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level == "warning");

            fixture.Time.Advance(TimeSpan.FromMilliseconds(100));
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(6, reads);
            Assert.True(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Equal(ReadFailure.InUseByAnotherProgram, fixture.ViewModel.UnreadableReason);
            Assert.True(DocumentRow(fixture.ViewModel, RepoDocA).IsUnreadable);
            Assert.Single(fixture.Surface.Shown);
            Assert.Single(fixture.Log.Entries, entry => entry.Level == "warning");
        });

    [Fact]
    public Task A_first_show_that_fails_in_use_shows_the_pane_at_once() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Reader = path => SamePath(path, RepoDocB) ? throw InUse(path) : fixture.Files[path];

            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocB);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Equal(ReadFailure.InUseByAnotherProgram, fixture.ViewModel.UnreadableReason);
            Assert.True(DocumentRow(fixture.ViewModel, RepoDocB).IsUnreadable);
        });

    [Fact]
    public Task An_access_denied_reload_shows_the_pane_at_once() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Reader = path => throw new UnauthorizedAccessException($"{path} is not yours.");

            await fixture.ReportAsync(RepoDocA);

            Assert.True(fixture.ViewModel.SelectedIsUnreadable);
            Assert.Equal(ReadFailure.AccessDenied, fixture.ViewModel.UnreadableReason);
            Assert.True(DocumentRow(fixture.ViewModel, RepoDocA).IsUnreadable);
        });

    [Fact]
    public Task The_2_s_tick_before_any_report_keeps_the_page_for_the_grace() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Absent.Add(RepoDocA);
            fixture.Files.Remove(RepoDocA);

            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            Dispatcher.UIThread.RunJobs();
            await fixture.ViewModel.WhenRendersSettled();
            Assert.False(fixture.ViewModel.SelectedIsMissing);
            Assert.False(DocumentRow(fixture.ViewModel, RepoDocA).IsMissing);

            fixture.Time.Advance(TimeSpan.FromMilliseconds(500));
            Dispatcher.UIThread.RunJobs();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.SelectedIsMissing);
            Assert.True(DocumentRow(fixture.ViewModel, RepoDocA).IsMissing);
            Assert.Single(fixture.Surface.Shown);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level is "warning" or "error");
        });

    [Fact]
    public Task Returning_to_a_document_inside_its_grace_shows_the_missing_pane() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Absent.Add(RepoDocA);
            fixture.Files.Remove(RepoDocA);
            await fixture.ReportAsync(RepoDocA);

            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocB);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoDocA);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(RepoDocA, fixture.ViewModel.SelectedPath);
            Assert.True(fixture.ViewModel.SelectedIsMissing);
            Assert.False(fixture.ViewModel.SelectedIsUnreadable);
            Assert.False(DocumentRow(fixture.ViewModel, RepoDocA).IsUnreadable);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level is "warning" or "error");
        });

    [Fact]
    public Task A_gone_read_that_ends_after_the_grace_starts_no_second_grace() =>
        Headless.RunAsync(async () =>
        {
            using var gate = new ManualResetEventSlim();
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Absent.Add(RepoDocA);
            fixture.Files.Remove(RepoDocA);
            await fixture.ReportAsync(RepoDocA);
            fixture.Reader = path =>
            {
                WaitFor(gate);
                return fixture.Files.TryGetValue(path, out string? text) ? text : throw new FileNotFoundException("No such fake file.", path);
            };

            fixture.RaiseFromPool(RepoDocA);
            Dispatcher.UIThread.RunJobs();
            fixture.Time.Advance(TimeSpan.FromMilliseconds(500));
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.ViewModel.SelectedIsMissing);
            gate.Set();
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Absent.Remove(RepoDocA);
            fixture.Files[RepoDocA] = "# Back on disk";

            await fixture.ReportAsync(RepoDocA);

            Assert.False(fixture.ViewModel.SelectedIsMissing);
            Assert.Equal(2, fixture.Surface.Shown.Count);
            Assert.Contains("Back on disk", fixture.Surface.Shown[1].Html, StringComparison.Ordinal);
        });

    [Fact]
    public Task A_second_report_inside_the_grace_does_not_extend_it() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Absent.Add(RepoDocA);
            fixture.Files.Remove(RepoDocA);
            await fixture.ReportAsync(RepoDocA);

            fixture.Time.Advance(TimeSpan.FromMilliseconds(300));
            await fixture.ReportAsync(RepoDocA);
            Assert.False(fixture.ViewModel.SelectedIsMissing);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(200));
            Dispatcher.UIThread.RunJobs();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.SelectedIsMissing);
            Assert.Single(fixture.Surface.Shown);
        });

    [Fact]
    public Task A_failed_SetPaths_is_retried_only_when_the_documents_change() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Changes.ThrowOnSetPaths = new InvalidOperationException("The watch was refused.");
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            fixture.ViewModel.ToggleSidebarCommand.Execute(null);
            Assert.Single(fixture.Changes.SetPathsCalls);
            Assert.Single(fixture.Log.Entries, entry => entry.Level == "error");

            fixture.ViewModel.Open([RepoDocB]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(2, fixture.Changes.SetPathsCalls.Count);
        });

    [Fact]
    public Task BuildSession_includes_sidebar_hidden_and_collapsed_groups() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA, AlphaDoc]);
            GroupRow(fixture.ViewModel, Alpha).IsExpanded = false;
            fixture.ViewModel.ToggleSidebarCommand.Execute(null);

            SessionState session = fixture.ViewModel.BuildSession(new WindowBounds(10, 20, 800, 600, false), 300);

            Assert.True(session.SidebarHidden);
            Assert.Equal([Alpha], session.CollapsedGroups);
            Assert.Equal([RepoDocA, AlphaDoc], session.OpenFiles);
            Assert.Equal(RepoDocA, session.SelectedFile);
            Assert.Equal(300, session.SidebarWidth);
        });

    [Fact]
    public Task A_document_whose_folder_has_a_width_shows_at_it() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Path.Combine(Repo, "docs"), ContentWidth.FromPx(640)));

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            ShownDocument shown = Assert.IsType<ShownDocument>(Assert.Single(fixture.Surface.Calls));
            Assert.Equal(RepoDocA, shown.DocumentPath);
            Assert.Equal(640, shown.ContentWidth);
        });

    [Fact]
    public Task A_document_no_folder_has_a_width_for_gets_the_app_default() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(800, Assert.Single(fixture.Surface.Shown).ContentWidth);
        });

    [Fact]
    public Task A_folder_set_to_full_shows_its_document_full_width() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Repo, ContentWidth.Full));

            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Null(Assert.Single(fixture.Surface.Shown).ContentWidth);
        });

    [Fact]
    public Task Selecting_another_document_resolves_its_width() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Path.Combine(Repo, "docs"), ContentWidth.FromPx(640)));
            Assert.True(fixture.Widths.Set(Alpha, ContentWidth.FromPx(720)));
            fixture.ViewModel.Open([RepoDocA, AlphaDoc]);
            await fixture.ViewModel.WhenRendersSettled();

            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, AlphaDoc);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(new double?[] { 640, 720 }, fixture.Surface.ShownWidths);
            Assert.Equal(AlphaDoc, fixture.Surface.Shown[^1].DocumentPath);
            Assert.Empty(fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task A_widths_file_another_instance_changed_is_read_at_the_next_render() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            var otherInstance = new WidthStore(fixture.WidthsFile, new RecordingAppLog());
            Assert.True(otherInstance.Set(Path.Combine(Repo, "docs"), ContentWidth.FromPx(600)));
            fixture.ViewModel.Reload();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(new double?[] { 800, 600 }, fixture.Surface.ShownWidths);
        });

    [Fact]
    public Task A_stale_resolve_sends_no_width() =>
        Headless.RunAsync(async () =>
        {
            using var gate = new ManualResetEventSlim();
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Path.Combine(Repo, "docs"), ContentWidth.FromPx(640)));
            Assert.True(fixture.Widths.Set(Repo, ContentWidth.FromPx(560)));
            fixture.Reader = path =>
            {
                if (SamePath(path, RepoDocA))
                {
                    WaitFor(gate);
                }

                return fixture.Files[path];
            };

            fixture.ViewModel.Open([RepoDocA, RepoRootDoc]);
            fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoRootDoc);
            gate.Set();
            await fixture.ViewModel.WhenRendersSettled();

            ShownDocument shown = Assert.IsType<ShownDocument>(Assert.Single(fixture.Surface.Calls));
            Assert.Equal(RepoRootDoc, shown.DocumentPath);
            Assert.Equal(560, shown.ContentWidth);
        });

    [Theory]
    [InlineData(760, true, 800)]
    [InlineData(763, false, 760)]
    public Task Stepping_the_width_writes_the_source_folder_and_sends_it(double from, bool wider, double expected) =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            string docs = Path.Combine(Repo, "docs");
            Assert.True(fixture.Widths.Set(docs, ContentWidth.FromPx(from)));
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            fixture.ViewModel.StepContentWidth(wider, WidePane);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(expected, fixture.Widths.Get(docs)?.Px);
            Assert.Equal(new ContentWidthSet(expected), fixture.Surface.Calls[^1]);
        });

    [Fact]
    public Task Stepping_an_inherited_width_writes_the_ancestor_it_comes_from() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Repo, ContentWidth.FromPx(760)));
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            fixture.ViewModel.StepContentWidth(wider: true, WidePane);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(800, fixture.Widths.Get(Repo)?.Px);
            Assert.Null(fixture.Widths.Get(Path.Combine(Repo, "docs")));
            Assert.Equal(760, Assert.Single(fixture.Surface.Shown).ContentWidth);
            Assert.Equal(new double?[] { 800 }, fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task Stepping_the_app_default_writes_the_default() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            fixture.ViewModel.StepContentWidth(wider: true, WidePane);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(840, fixture.Widths.Default.Px);
            Assert.Null(fixture.Widths.Get(Path.Combine(Repo, "docs")));
            Assert.Equal(800, Assert.Single(fixture.Surface.Shown).ContentWidth);
            Assert.Equal(new double?[] { 840 }, fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task Stepping_down_from_full_goes_to_the_largest_grid_value_below_the_text_space() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            string docs = Path.Combine(Repo, "docs");
            Assert.True(fixture.Widths.Set(docs, ContentWidth.Full));
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            fixture.ViewModel.StepContentWidth(wider: false, paneWidth: 1000);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(880, fixture.Widths.Get(docs)?.Px);
            Assert.Null(Assert.Single(fixture.Surface.Shown).ContentWidth);
            Assert.Equal(new double?[] { 880 }, fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task A_step_the_store_cannot_write_is_logged_and_sends_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            File.WriteAllText(fixture.WidthsFolder, "a file where the widths folder should be");
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            int errorsBefore = fixture.Log.Entries.Count(entry => entry.Level == "error");

            fixture.ViewModel.StepContentWidth(wider: true, WidePane);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Empty(fixture.Surface.ContentWidths);
            Assert.True(fixture.Log.Entries.Count(entry => entry.Level == "error") > errorsBefore);
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "warning" && entry.Message.Contains(RepoDocA, StringComparison.Ordinal));
            Assert.Equal((ToastKind.Warning, "Couldn't save the width; see the log"), Assert.Single(fixture.Toasts.Shown));
        });

    [Fact]
    public Task A_step_that_throws_is_logged_as_an_error_and_toasted() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            fixture.ViewModel.StepContentWidth(wider: true, double.NaN);
            await fixture.ViewModel.WhenRendersSettled();

            LogEntry error = Assert.Single(fixture.Log.Entries, entry => entry.Level == "error");
            Assert.Equal($"Cannot step the content width of {RepoDocA}.", error.Message);
            Assert.IsType<ArgumentOutOfRangeException>(error.Exception);
            Assert.Equal((ToastKind.Warning, "Couldn't save the width; see the log"), Assert.Single(fixture.Toasts.Shown));
        });

    [Fact]
    public Task A_step_whose_write_fails_after_the_window_closed_is_logged_once_and_throws_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture(ownToasts: true);
            File.WriteAllText(fixture.WidthsFolder, "a file where the widths folder should be");
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            fixture.ViewModel.StepContentWidth(wider: true, WidePane);
            fixture.ViewModel.Dispose();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Single(fixture.Log.Entries, entry => entry.Message.StartsWith("Cannot save the stepped content width", StringComparison.Ordinal));
            Assert.DoesNotContain(
                fixture.Log.Entries,
                entry => entry.Message.StartsWith("Cannot step", StringComparison.Ordinal) || entry.Message == "Cannot change the content width."
            );
        });

    [Fact]
    public Task Stepping_with_no_document_shown_writes_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();

            fixture.ViewModel.StepContentWidth(wider: true, WidePane);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(File.Exists(fixture.WidthsFile));
            Assert.Empty(fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task A_render_held_by_the_page_never_shows_its_document_after_the_selection_moved() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Path.Combine(Repo, "docs"), ContentWidth.FromPx(640)));
            Assert.True(fixture.Widths.Set(Repo, ContentWidth.FromPx(560)));
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Surface.HoldNextCall = release;
            try
            {
                fixture.ViewModel.Open([RepoDocA, RepoRootDoc]);
                await fixture.Surface.CallHeld.Task.WaitAsync(GateBackstop, TestContext.Current.CancellationToken);
                int callsBeforeMove = fixture.Surface.Calls.Count;
                fixture.ViewModel.SelectedItem = DocumentRow(fixture.ViewModel, RepoRootDoc);
                release.SetResult();
                await fixture.ViewModel.WhenRendersSettled();

                Assert.DoesNotContain(
                    fixture.Surface.Calls.Skip(callsBeforeMove),
                    call => call is ShownDocument shown && shown.DocumentPath == RepoDocA
                );
                Assert.Equal(RepoRootDoc, fixture.Surface.Shown[^1].DocumentPath);
            }
            finally
            {
                release.TrySetResult();
            }
        });

    [Fact]
    public Task Two_presses_in_one_turn_step_twice() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            string docs = Path.Combine(Repo, "docs");
            Assert.True(fixture.Widths.Set(docs, ContentWidth.FromPx(760)));
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();

            fixture.ViewModel.StepContentWidth(wider: true, WidePane);
            fixture.ViewModel.StepContentWidth(wider: true, WidePane);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(840, fixture.Widths.Get(docs)?.Px);
            Assert.Equal(new double?[] { 800, 840 }, fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task Stepping_a_document_that_went_missing_writes_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Absent.Add(RepoDocA);
            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            fixture.Time.Advance(TimeSpan.FromMilliseconds(500));
            Assert.True(fixture.ViewModel.SelectedIsMissing);

            fixture.ViewModel.StepContentWidth(wider: true, WidePane);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(File.Exists(fixture.WidthsFile));
            Assert.Empty(fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task Stepping_a_document_that_became_unreadable_writes_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([RepoDocA]);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Reader = path => throw new IOException($"{path} is locked.", unchecked((int)0x80070020));
            fixture.ViewModel.Reload();
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.ViewModel.SelectedIsUnreadable);

            fixture.ViewModel.StepContentWidth(wider: true, WidePane);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(File.Exists(fixture.WidthsFile));
            Assert.Empty(fixture.Surface.ContentWidths);
        });

    private static string P(params string[] parts) => Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(parts));

    private static bool SamePath(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    /// <summary>The read failure Windows reports while another program holds the file: a sharing violation.</summary>
    private static IOException InUse(string path) => new($"{path} is in use.", unchecked((int)0x80070020));

    /// <summary>Waits for a test's gate, failing the read (and so the test) if it is never opened.</summary>
    private static void WaitFor(ManualResetEventSlim gate)
    {
        if (!gate.Wait(GateBackstop, TestContext.Current.CancellationToken))
        {
            throw new TimeoutException($"The test gate was not opened within {GateBackstop.TotalSeconds} s.");
        }
    }

    /// <summary>Waits for one release of <paramref name="signal"/>, failing the test if none comes.</summary>
    private static void WaitFor(SemaphoreSlim signal)
    {
        if (!signal.Wait(GateBackstop, TestContext.Current.CancellationToken))
        {
            throw new TimeoutException($"The test signal was not released within {GateBackstop.TotalSeconds} s.");
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

    /// <summary>
    /// A view model over fake files, a fake surface, a fake clock and a widths file in a temp folder that exists only once a
    /// test writes a width. Disposing waits for every render and step before it deletes the folder.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private int _existsCalls;

        /// <param name="persist">Whether the window's state persists, as a main window's does.</param>
        /// <param name="ownToasts">
        /// True to give the view model no toasts, so it makes and disposes its own <see cref="ToastCenter"/>; else it shows
        /// them on <see cref="Toasts"/>.
        /// </param>
        public Fixture(bool persist = true, bool ownToasts = false)
        {
            WidthsFolder = Path.Combine(Path.GetTempPath(), "yamv-vm-widths-" + Guid.NewGuid().ToString("N"));
            WidthsFile = Path.Combine(WidthsFolder, "widths.json");
            Widths = new WidthStore(WidthsFile, Log);
            Reader = path => Files.TryGetValue(path, out string? text) ? text : throw new FileNotFoundException("No such fake file.", path);
            State = new DocumentWindowState(persist, FindRepo, Exists, Plain);
            ViewModel = new DocumentWindowViewModel(
                State,
                Surface,
                new DocumentWindowServices(Widths, path => Reader(path), Time, Log, Changes, ownToasts ? null : Toasts)
            );
        }

        /// <summary>The toasts the view model shows.</summary>
        public RecordingToasts Toasts { get; } = new();

        /// <summary>What the view model follows its documents' changes through.</summary>
        public FakeDocumentChanges Changes { get; } = new();

        /// <summary>The folder of <see cref="WidthsFile"/>; not created until a width is written.</summary>
        public string WidthsFolder { get; }

        public string WidthsFile { get; }

        /// <summary>The view model's width store.</summary>
        public WidthStore Widths { get; }

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

        /// <summary>
        /// Raises a change report naming <paramref name="paths"/> on a thread-pool thread, as the watcher does, and returns once it
        /// was raised. The UI thread is blocked meanwhile, so the work the report posts waits until the test pumps it.
        /// </summary>
        public void RaiseFromPool(params string[] paths)
        {
            // Not disposed: the pool thread may still be inside Set when the wait returns.
            var raised = new ManualResetEventSlim();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Changes.Raise(paths);
                }
                finally
                {
                    raised.Set();
                }
            });
            WaitFor(raised);
        }

        /// <summary>Raises a change report from a pool thread, runs what it posted to the UI thread, and waits for the renders.</summary>
        public async Task ReportAsync(params string[] paths)
        {
            RaiseFromPool(paths);
            Dispatcher.UIThread.RunJobs();
            await ViewModel.WhenRendersSettled();
        }

        public async ValueTask DisposeAsync()
        {
            ViewModel.Dispose();
            await ViewModel.WhenRendersSettled().WaitAsync(GateBackstop, TestContext.Current.CancellationToken);
            if (Directory.Exists(WidthsFolder))
            {
                Directory.Delete(WidthsFolder, recursive: true);
            }
            else if (File.Exists(WidthsFolder))
            {
                File.Delete(WidthsFolder);
            }
        }

        private bool Exists(string path)
        {
            Interlocked.Increment(ref _existsCalls);
            return !Absent.Contains(path);
        }
    }
}
