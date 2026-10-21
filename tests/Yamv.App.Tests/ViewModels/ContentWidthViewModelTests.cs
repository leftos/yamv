using Microsoft.Extensions.Time.Testing;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Widths;

namespace Yamv.App.Tests.ViewModels;

public sealed class ContentWidthViewModelTests
{
    private const double WidePane = 1600;
    private static readonly double WideTextSpace = ContentWidth.TextSpace(WidePane);
    private static readonly TimeSpan GateBackstop = TimeSpan.FromSeconds(30);
    private static readonly GroupingOptions Plain = new(null, StringComparison.OrdinalIgnoreCase);
    private static readonly string Dev = P("dev");
    private static readonly string Repo = P("dev", "yamv");
    private static readonly string Docs = Path.Combine(Repo, "docs");
    private static readonly string Plans = Path.Combine(Docs, "plans");
    private static readonly string DocA = Path.Combine(Docs, "a.md");
    private static readonly string DocB = Path.Combine(Docs, "b.md");
    private static readonly string PlanDoc = Path.Combine(Plans, "v1-build.md");
    private static readonly string Alpha = P("dev", "alpha");

    [Fact]
    public Task Moving_the_slider_sends_live_widths_and_writes_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);
            byte[] before = File.ReadAllBytes(fixture.WidthsFile);

            popover.SliderPosition = 0;
            popover.SliderPosition = 0.5;
            popover.SliderPosition = 1;
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(new double?[] { 480, Math.Round(480 + (0.5 * (WideTextSpace - 480))), null }, fixture.Surface.ContentWidths);
            Assert.Equal("Full", popover.Readout);
            Assert.Equal(before, File.ReadAllBytes(fixture.WidthsFile));
        });

    [Fact]
    public Task Opening_shows_the_documents_width_on_its_source_segment() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));

            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);

            Assert.Equal(["docs", "yamv (repo)", "All documents"], Labels(popover));
            Assert.Equal(Docs, popover.SelectedSegment?.Folder);
            Assert.Equal("760 px", popover.Readout);
            Assert.Equal(ContentWidth.FromPx(760).ToSlider(WideTextSpace), popover.SliderPosition, 6);
            Assert.True(popover.IsSliderEnabled);
            Assert.Null(popover.SliderCaption);
            Assert.Null(popover.InheritedFrom);
            Assert.True(popover.CanReset);
            Assert.Empty(fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task End_of_a_drag_writes_the_selected_segments_width_once() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);

            popover.SliderPosition = 0.2;
            popover.SliderPosition = 0;
            popover.EndDrag();
            await fixture.ViewModel.WhenRendersSettled();
            Assert.Equal(480, fixture.Widths.Get(Docs)?.Px);
            Assert.True(new WidthStore(fixture.WidthsFile, fixture.Log).Set(Docs, ContentWidth.FromPx(600)));
            popover.EndDrag();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(600, fixture.Widths.Get(Docs)?.Px);
            Assert.Null(fixture.Widths.Get(Repo));
            Assert.Equal(ContentWidth.DefaultPx, fixture.Widths.Default.Px);
            Assert.Equal(480, fixture.Surface.ContentWidths[^1]);
            Assert.Null(popover.FootMessage);
        });

    [Fact]
    public Task All_documents_writes_the_app_default() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);
            Assert.True(popover.SelectedSegment?.IsAllDocuments);

            popover.SliderPosition = 0;
            popover.EndDrag();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(480, fixture.Widths.Default.Px);
            Assert.Null(fixture.Widths.Get(Docs));
            Assert.Null(fixture.Widths.Get(Repo));
        });

    [Fact]
    public Task Switching_to_a_narrower_segment_then_dragging_writes_an_override_there() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(720)));
            ContentWidthViewModel popover = await fixture.OpenAsync(PlanDoc);
            Assert.Equal(["plans", "docs", "yamv (repo)", "All documents"], Labels(popover));
            Assert.Equal(Docs, popover.SelectedSegment?.Folder);

            popover.SelectSegmentCommand.Execute(popover.Segments[0]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(Plans, popover.SelectedSegment?.Folder);
            Assert.Equal("docs", popover.InheritedFrom);
            Assert.False(popover.CanReset);
            Assert.Equal("720 px", popover.Readout);

            popover.SliderPosition = 0;
            popover.EndDrag();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(480, fixture.Widths.Get(Plans)?.Px);
            Assert.Equal(720, fixture.Widths.Get(Docs)?.Px);
            Assert.Null(popover.InheritedFrom);
            Assert.True(popover.CanReset);
        });

    [Fact]
    public Task Reset_removes_the_override_and_the_page_gets_the_inherited_width() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Repo, ContentWidth.FromPx(560)));
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);
            Assert.True(popover.ResetCommand.CanExecute(null));

            popover.ResetCommand.Execute(null);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Null(fixture.Widths.Get(Docs));
            Assert.Equal(560, fixture.Widths.Get(Repo)?.Px);
            Assert.Equal(560, fixture.Surface.ContentWidths[^1]);
            Assert.Equal("560 px", popover.Readout);
            Assert.Equal(Repo, popover.SelectedSegment?.Folder);
        });

    [Fact]
    public Task Reset_is_disabled_on_a_segment_without_its_own_width() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);

            Assert.False(popover.CanReset);
            Assert.False(popover.ResetCommand.CanExecute(null));
        });

    [Fact]
    public Task Clear_all_clears_at_and_below_the_group_root() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Dev, ContentWidth.FromPx(900)));
            Assert.True(fixture.Widths.Set(Repo, ContentWidth.FromPx(560)));
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            Assert.True(fixture.Widths.Set(Alpha, ContentWidth.FromPx(600)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);

            popover.ClearAllCommand.Execute(null);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Null(fixture.Widths.Get(Docs));
            Assert.Null(fixture.Widths.Get(Repo));
            Assert.Equal(900, fixture.Widths.Get(Dev)?.Px);
            Assert.Equal(600, fixture.Widths.Get(Alpha)?.Px);
            Assert.Equal(900, fixture.Surface.ContentWidths[^1]);
            Assert.Equal(["docs", "yamv (repo)", "dev", "All documents"], Labels(popover));
            Assert.Equal(Dev, popover.SelectedSegment?.Folder);
        });

    [Fact]
    public Task A_failed_write_springs_back_and_shows_the_message_for_4_s() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(560)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);

            using (new FileStream(fixture.WidthsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                popover.SliderPosition = 1;
                popover.EndDrag();
                await fixture.ViewModel.WhenRendersSettled();
            }

            Assert.Equal(560, fixture.Widths.Get(Docs)?.Px);
            Assert.Equal("560 px", popover.Readout);
            Assert.Equal(ContentWidth.FromPx(560).ToSlider(WideTextSpace), popover.SliderPosition, 6);
            Assert.Equal(new double?[] { null, 560 }, fixture.Surface.ContentWidths);
            Assert.Equal("Could not save the width; see the log.", popover.FootMessage);
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "warning" && entry.Message.Contains(DocA, StringComparison.Ordinal));

            fixture.Time.Advance(TimeSpan.FromMilliseconds(3900));
            Assert.Equal(ContentWidthViewModel.SaveFailedMessage, popover.FootMessage);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(100));
            Assert.Null(popover.FootMessage);
        });

    [Fact]
    public Task Queued_width_work_waits_for_the_work_before_it() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool ran = false;

            fixture.ViewModel.QueueWidthWork(() => release.Task);
            fixture.ViewModel.QueueWidthWork(() =>
            {
                ran = true;
                return Task.CompletedTask;
            });
            await Task.Yield();

            Assert.False(ran);
            release.SetResult();
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(ran);
        });

    [Fact]
    public Task Queued_width_work_runs_after_work_that_threw() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            bool ran = false;

            fixture.ViewModel.QueueWidthWork(() => throw new InvalidOperationException("Work A failed."));
            fixture.ViewModel.QueueWidthWork(() =>
            {
                ran = true;
                return Task.CompletedTask;
            });
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(ran);
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "error" && entry.Exception is InvalidOperationException);
        });

    [Fact]
    public Task Width_work_queued_from_inside_work_runs_after_it() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            List<string> order = [];

            fixture.ViewModel.QueueWidthWork(() =>
            {
                order.Add("A starts");
                fixture.ViewModel.QueueWidthWork(() =>
                {
                    order.Add("C");
                    return Task.CompletedTask;
                });
                order.Add("A ends");
                return Task.CompletedTask;
            });
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(["A starts", "A ends", "C"], order);
        });

    [Fact]
    public Task A_read_landing_mid_drag_keeps_the_drag_and_its_end_saves_to_the_new_segment() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(720)));
            ContentWidthViewModel popover = await fixture.OpenAsync(PlanDoc);
            TaskCompletionSource hold = fixture.HoldQueue();

            popover.SelectSegmentCommand.Execute(popover.Segments[0]);
            popover.SliderPosition = 0;
            hold.SetResult();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(Plans, popover.SelectedSegment?.Folder);
            Assert.Equal("480 px", popover.Readout);
            Assert.Equal(0, popover.SliderPosition);
            popover.EndDrag();
            await fixture.ViewModel.WhenRendersSettled();
            Assert.Equal(480, fixture.Widths.Get(Plans)?.Px);
            Assert.Equal(720, fixture.Widths.Get(Docs)?.Px);
            Assert.Equal(480, fixture.Surface.ContentWidths[^1]);
        });

    [Fact]
    public Task A_save_landing_during_a_second_drag_keeps_that_drag() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);
            TaskCompletionSource hold = fixture.HoldQueue();

            popover.SliderPosition = 0;
            popover.EndDrag();
            popover.SliderPosition = 1;
            hold.SetResult();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(480, fixture.Widths.Get(Docs)?.Px);
            Assert.Equal("Full", popover.Readout);
            Assert.Equal(1, popover.SliderPosition);
            Assert.Null(fixture.Surface.ContentWidths[^1]);
            popover.EndDrag();
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.Widths.Get(Docs)?.IsFull);
        });

    [Fact]
    public Task A_drag_back_to_the_saved_width_writes_no_override() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(720)));
            ContentWidthViewModel popover = await fixture.OpenAsync(PlanDoc);
            popover.SelectSegmentCommand.Execute(popover.Segments[0]);
            await fixture.ViewModel.WhenRendersSettled();
            double start = popover.SliderPosition;

            popover.SliderPosition = 0;
            popover.SliderPosition = start;
            popover.EndDrag();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Null(fixture.Widths.Get(Plans));
            Assert.Equal(720, fixture.Surface.ContentWidths[^1]);
        });

    [Fact]
    public Task Closing_mid_drag_restores_the_page_and_writes_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);
            byte[] before = File.ReadAllBytes(fixture.WidthsFile);

            popover.SliderPosition = 0;
            popover.Close();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(popover.IsStale);
            Assert.Equal(new double?[] { 480, 760 }, fixture.Surface.ContentWidths);
            Assert.Equal(before, File.ReadAllBytes(fixture.WidthsFile));
        });

    [Fact]
    public Task Closing_releases_the_foot_timer_and_the_windows_reference() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(560)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);
            int timersBefore = fixture.Timers.Created.Count;
            using (new FileStream(fixture.WidthsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                popover.SliderPosition = 1;
                popover.EndDrag();
                await fixture.ViewModel.WhenRendersSettled();
            }

            TrackedTimer footTimer = Assert.Single(fixture.Timers.Created.Skip(timersBefore));
            Assert.False(footTimer.IsDisposed);
            Assert.Same(popover, fixture.ViewModel.ContentWidthPopover);
            popover.Close();
            fixture.Time.Advance(TimeSpan.FromSeconds(5));

            Assert.True(footTimer.IsDisposed);
            Assert.Null(fixture.ViewModel.ContentWidthPopover);
            Assert.Equal(ContentWidthViewModel.SaveFailedMessage, popover.FootMessage);
        });

    [Fact]
    public Task A_save_that_fails_after_close_is_logged_and_the_page_gets_the_stored_width() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(560)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);
            TaskCompletionSource hold = fixture.HoldQueue();

            popover.SliderPosition = 1;
            popover.EndDrag();
            popover.Close();
            using (new FileStream(fixture.WidthsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                hold.SetResult();
                await fixture.ViewModel.WhenRendersSettled();
            }

            Assert.Equal(560, fixture.Widths.Get(Docs)?.Px);
            Assert.Equal(new double?[] { null, 560 }, fixture.Surface.ContentWidths);
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "warning" && entry.Message.Contains(DocA, StringComparison.Ordinal));
            Assert.Null(popover.FootMessage);
        });

    [Fact]
    public Task Going_stale_mid_drag_gives_the_page_back_the_stored_width() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);
            byte[] before = File.ReadAllBytes(fixture.WidthsFile);

            popover.SliderPosition = 0;
            await fixture.CreateAsync();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(popover.IsStale);
            Assert.Equal(new double?[] { 480, 760 }, fixture.Surface.ContentWidths);
            Assert.Equal(before, File.ReadAllBytes(fixture.WidthsFile));
        });

    [Fact]
    public Task A_save_that_changes_nothing_when_it_runs_writes_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(720)));
            ContentWidthViewModel popover = await fixture.OpenAsync(PlanDoc);
            Assert.Equal(Docs, popover.SelectedSegment?.Folder);
            WidthScope repo = popover.Segments[2];
            Assert.Equal(Repo, repo.Folder);
            TaskCompletionSource hold = fixture.HoldQueue();

            popover.SelectSegmentCommand.Execute(repo);
            popover.SliderPosition = ContentWidth.AppDefault.ToSlider(WideTextSpace);
            Assert.Equal(ReadoutPx(ContentWidth.DefaultPx), popover.Readout);
            popover.EndDrag();
            hold.SetResult();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(Repo, popover.SelectedSegment?.Folder);
            Assert.Null(fixture.Widths.Get(Repo));
            Assert.Equal(720, fixture.Widths.Get(Docs)?.Px);
            Assert.Equal("All documents", popover.InheritedFrom);
        });

    [Fact]
    public Task A_read_queued_before_a_drags_save_leaves_the_slider_at_the_drag() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(720)));
            ContentWidthViewModel popover = await fixture.OpenAsync(PlanDoc);
            TaskCompletionSource beforeRead = fixture.HoldQueue();

            popover.SelectSegmentCommand.Execute(popover.Segments[0]);
            popover.SliderPosition = 0;
            (TaskCompletionSource beforeSave, Task saveIsNext) = fixture.HoldQueueAndSignal();
            try
            {
                popover.EndDrag();
                beforeRead.SetResult();
                await saveIsNext.WaitAsync(GateBackstop, TestContext.Current.CancellationToken);

                Assert.Equal(Plans, popover.SelectedSegment?.Folder);
                Assert.Equal("480 px", popover.Readout);
                Assert.Equal(0, popover.SliderPosition);
            }
            finally
            {
                beforeSave.TrySetResult();
            }

            await fixture.ViewModel.WhenRendersSettled();
            Assert.Equal(480, fixture.Widths.Get(Plans)?.Px);
            Assert.Equal("480 px", popover.Readout);
            Assert.Equal(480, fixture.Surface.ContentWidths[^1]);
        });

    [Fact]
    public Task A_drag_left_by_a_selection_change_sends_nothing_to_the_next_documents_page() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            fixture.ViewModel.Open([DocA, DocB]);
            await fixture.ViewModel.WhenRendersSettled();
            ContentWidthViewModel popover = await fixture.CreateAsync();

            popover.SliderPosition = 0;
            fixture.ViewModel.SelectNext();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(DocB, fixture.ViewModel.SelectedPath);
            Assert.True(popover.IsStale);
            Assert.Equal(new double?[] { 480 }, fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task A_resend_queued_before_a_selection_change_sends_nothing_to_the_next_documents_page() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            fixture.ViewModel.Open([DocA, DocB]);
            await fixture.ViewModel.WhenRendersSettled();
            ContentWidthViewModel popover = await fixture.CreateAsync();
            TaskCompletionSource hold = fixture.HoldQueue();

            popover.SliderPosition = 0;
            popover.Close();
            fixture.ViewModel.SelectNext();
            hold.SetResult();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(DocB, fixture.ViewModel.SelectedPath);
            Assert.Equal(new double?[] { 480 }, fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task A_save_failing_after_a_selection_change_is_logged_and_sends_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(560)));
            fixture.ViewModel.Open([DocA, DocB]);
            await fixture.ViewModel.WhenRendersSettled();
            ContentWidthViewModel popover = await fixture.CreateAsync();
            TaskCompletionSource hold = fixture.HoldQueue();

            popover.SliderPosition = 1;
            popover.EndDrag();
            fixture.ViewModel.SelectNext();
            using (new FileStream(fixture.WidthsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                hold.SetResult();
                await fixture.ViewModel.WhenRendersSettled();
            }

            Assert.Equal(DocB, fixture.ViewModel.SelectedPath);
            Assert.Equal(560, fixture.Widths.Get(Docs)?.Px);
            Assert.Equal(new double?[] { null }, fixture.Surface.ContentWidths);
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "warning" && entry.Message.Contains(DocA, StringComparison.Ordinal));
        });

    [Fact]
    public Task Disposing_mid_drag_reads_no_width() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);

            popover.SliderPosition = 0;
            // While the file is locked, any read of the store logs that it cannot read the widths file.
            using (new FileStream(fixture.WidthsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fixture.ViewModel.Dispose();
                await fixture.ViewModel.WhenRendersSettled();
            }

            Assert.True(popover.IsStale);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Message.Contains("Cannot read the widths file", StringComparison.Ordinal));
            Assert.Equal(new double?[] { 480 }, fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task A_reset_queued_before_a_drags_save_sends_its_width_to_no_page() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Repo, ContentWidth.FromPx(560)));
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);
            TaskCompletionSource hold = fixture.HoldQueue();

            popover.ResetCommand.Execute(null);
            popover.SliderPosition = 0;
            popover.EndDrag();
            hold.SetResult();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Null(fixture.Widths.Get(Docs));
            Assert.Equal(480, fixture.Widths.Get(Repo)?.Px);
            Assert.Equal(new double?[] { 480, 480 }, fixture.Surface.ContentWidths);
            Assert.Equal("480 px", popover.Readout);
        });

    [Fact]
    public Task A_disposed_window_shows_no_editable_document() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([DocA]);
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.ViewModel.IsShowingContentWidthOf(DocA));

            fixture.ViewModel.Dispose();

            Assert.False(fixture.ViewModel.IsShowingContentWidthOf(DocA));
            Assert.Null(await fixture.ViewModel.CreateContentWidthPopoverAsync(WidePane));
        });

    [Fact]
    public Task A_step_whose_page_call_never_completes_does_not_block_a_popover_open() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([DocA]);
            await fixture.ViewModel.WhenRendersSettled();
            var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Surface.HoldNextCall = never;
            try
            {
                fixture.ViewModel.StepContentWidth(wider: true, WidePane);
                ContentWidthViewModel? popover = await fixture
                    .ViewModel.CreateContentWidthPopoverAsync(WidePane)
                    .WaitAsync(GateBackstop, TestContext.Current.CancellationToken);

                Assert.NotNull(popover);
                Assert.Equal("840 px", popover.Readout);
            }
            finally
            {
                never.TrySetResult();
            }
        });

    [Fact]
    public Task A_step_refreshes_the_open_popover() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);

            fixture.ViewModel.StepContentWidth(wider: true, WidePane);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal("800 px", popover.Readout);
            Assert.False(popover.IsStale);
        });

    [Fact]
    public Task A_drag_on_a_segment_that_cannot_reach_the_document_moves_no_page_but_still_saves() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Plans, ContentWidth.FromPx(600)));
            ContentWidthViewModel popover = await fixture.OpenAsync(PlanDoc);
            Assert.True(popover.DragReachesDocument);

            popover.SelectSegmentCommand.Execute(popover.Segments[1]);
            await fixture.ViewModel.WhenRendersSettled();
            Assert.Equal(Repo, popover.SelectedSegment?.Folder);
            Assert.False(popover.DragReachesDocument);
            popover.SliderPosition = 0;
            popover.EndDrag();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(480, fixture.Widths.Get(Repo)?.Px);
            Assert.Equal(600, fixture.Widths.Get(Plans)?.Px);
            Assert.DoesNotContain(480, fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task A_drag_on_a_segment_that_reaches_the_document_moves_the_page() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(720)));
            ContentWidthViewModel popover = await fixture.OpenAsync(PlanDoc);
            popover.SelectSegmentCommand.Execute(popover.Segments[0]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(popover.DragReachesDocument);
            popover.SliderPosition = 0;
            Assert.Equal(new double?[] { 480 }, fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task Reset_writes_even_when_the_popover_went_stale_before_it_ran() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            fixture.ViewModel.Open([DocA, DocB]);
            await fixture.ViewModel.WhenRendersSettled();
            ContentWidthViewModel popover = await fixture.CreateAsync();
            TaskCompletionSource hold = fixture.HoldQueue();

            popover.ResetCommand.Execute(null);
            fixture.ViewModel.SelectNext();
            hold.SetResult();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(popover.IsStale);
            Assert.Null(fixture.Widths.Get(Docs));
        });

    [Fact]
    public Task Picking_a_segment_that_a_queued_clear_removes_is_ignored() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(720)));
            ContentWidthViewModel popover = await fixture.OpenAsync(PlanDoc);
            WidthScope docs = popover.Segments[1];
            TaskCompletionSource hold = fixture.HoldQueue();

            popover.ClearAllCommand.Execute(null);
            popover.SelectSegmentCommand.Execute(docs);
            hold.SetResult();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(["docs/plans", "yamv (repo)", "All documents"], Labels(popover));
            Assert.True(popover.SelectedSegment?.IsAllDocuments);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level == "error");
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task A_failed_reset_or_clear_all_shows_the_message_and_logs_it(bool reset) =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);

            using (new FileStream(fixture.WidthsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                (reset ? popover.ResetCommand : popover.ClearAllCommand).Execute(null);
                await fixture.ViewModel.WhenRendersSettled();
            }

            Assert.Equal(760, fixture.Widths.Get(Docs)?.Px);
            Assert.Equal(ContentWidthViewModel.SaveFailedMessage, popover.FootMessage);
            Assert.False(popover.IsStale);
            LogEntry warning = Assert.Single(fixture.Log.Entries, entry => entry.Level == "warning");
            Assert.Contains(DocA, warning.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("slider", warning.Message, StringComparison.Ordinal);
        });

    [Fact]
    public Task Popover_goes_stale_when_its_document_goes_missing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);

            fixture.Absent.Add(DocA);
            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            Assert.False(popover.IsStale);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(500));

            Assert.True(popover.IsStale);
            Assert.Null(fixture.ViewModel.ContentWidthPopover);
        });

    [Fact]
    public Task Popover_goes_stale_when_its_document_becomes_unreadable() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);
            fixture.Reader = path => throw new IOException($"{path} is locked.", unchecked((int)0x80070020));

            fixture.ViewModel.Reload();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(popover.IsStale);
        });

    [Fact]
    public Task A_newer_popover_makes_the_older_one_stale() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            ContentWidthViewModel older = await fixture.OpenAsync(DocA);

            ContentWidthViewModel newer = await fixture.CreateAsync();

            Assert.True(older.IsStale);
            Assert.False(newer.IsStale);
            Assert.Same(newer, fixture.ViewModel.ContentWidthPopover);
        });

    [Fact]
    public Task Disposing_the_window_makes_the_popover_stale() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);

            fixture.ViewModel.Dispose();

            Assert.True(popover.IsStale);
        });

    [Fact]
    public Task Viewer_with_a_tree_uses_the_trees_group_root() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture(persist: false);
            fixture.ViewModel.Open([DocA, DocB]);
            await fixture.ViewModel.WhenRendersSettled();

            ContentWidthViewModel popover = await fixture.CreateAsync();

            Assert.Equal(["docs", "yamv (repo)", "All documents"], Labels(popover));
        });

    [Fact]
    public Task Viewer_popover_goes_stale_when_the_tree_appears() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture(persist: false);
            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);

            fixture.ViewModel.Open([DocA, DocB]);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(DocA, fixture.ViewModel.SelectedPath);
            Assert.True(fixture.ViewModel.ShowTree);
            Assert.True(popover.IsStale);
        });

    [Fact]
    public Task No_popover_for_an_empty_window() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();

            Assert.False(fixture.ViewModel.CanEditContentWidth);
            Assert.Null(await fixture.ViewModel.CreateContentWidthPopoverAsync(WidePane));
        });

    [Fact]
    public Task No_popover_for_a_missing_document_and_can_edit_follows() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            List<string?> changes = fixture.RecordChanges();
            fixture.ViewModel.Open([DocA]);
            await fixture.ViewModel.WhenRendersSettled();
            Assert.True(fixture.ViewModel.CanEditContentWidth);
            Assert.Contains(nameof(DocumentWindowViewModel.CanEditContentWidth), changes);
            changes.Clear();

            fixture.Absent.Add(DocA);
            fixture.Time.Advance(TimeSpan.FromSeconds(2));
            Assert.False(fixture.ViewModel.SelectedIsMissing);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(500));

            Assert.True(fixture.ViewModel.SelectedIsMissing);
            Assert.False(fixture.ViewModel.CanEditContentWidth);
            Assert.Contains(nameof(DocumentWindowViewModel.CanEditContentWidth), changes);
            Assert.Null(await fixture.ViewModel.CreateContentWidthPopoverAsync(WidePane));
        });

    [Fact]
    public Task No_popover_for_an_unreadable_document_and_can_edit_follows() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([DocA]);
            await fixture.ViewModel.WhenRendersSettled();
            List<string?> changes = fixture.RecordChanges();
            fixture.Reader = path => throw new IOException($"{path} is locked.", unchecked((int)0x80070020));

            fixture.ViewModel.Reload();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.SelectedIsUnreadable);
            Assert.False(fixture.ViewModel.CanEditContentWidth);
            Assert.Contains(nameof(DocumentWindowViewModel.CanEditContentWidth), changes);
            Assert.Null(await fixture.ViewModel.CreateContentWidthPopoverAsync(WidePane));
        });

    [Fact]
    public Task Popover_goes_stale_when_the_selection_moves_and_then_does_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(Docs, ContentWidth.FromPx(760)));
            fixture.ViewModel.Open([DocA, DocB]);
            await fixture.ViewModel.WhenRendersSettled();
            ContentWidthViewModel popover = await fixture.CreateAsync();
            Assert.False(popover.IsStale);

            fixture.ViewModel.SelectNext();
            await fixture.ViewModel.WhenRendersSettled();
            popover.SliderPosition = 0;
            popover.EndDrag();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(popover.IsStale);
            Assert.Empty(fixture.Surface.ContentWidths);
            Assert.Equal(760, fixture.Widths.Get(Docs)?.Px);
        });

    [Fact]
    public Task Viewer_popover_has_the_documents_folder_and_all_documents() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture(persist: false);

            ContentWidthViewModel popover = await fixture.OpenAsync(DocA);

            Assert.Equal(["docs", "All documents"], Labels(popover));
        });

    [Theory]
    [InlineData(560, false)]
    [InlineData(568, true)]
    public Task Narrow_pane_disables_the_slider_with_the_caption(double paneWidth, bool enabled) =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.ViewModel.Open([DocA]);
            await fixture.ViewModel.WhenRendersSettled();
            ContentWidthViewModel popover = await fixture.CreateAsync(paneWidth);

            popover.SliderPosition = 1;
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(enabled, popover.IsSliderEnabled);
            Assert.Equal(enabled ? null : "The window is narrower than 480 px", popover.SliderCaption);
            Assert.Equal(enabled ? 1 : 0, fixture.Surface.ContentWidths.Count);
        });

    private static string P(params string[] parts) => Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(parts));

    private static string ReadoutPx(double px) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{px:0} px");

    private static string[] Labels(ContentWidthViewModel popover) => [.. popover.Segments.Select(segment => segment.Label)];

    private static string? FindRepo(string path) =>
        path.StartsWith(Repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Repo
        : path.StartsWith(Alpha + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Alpha
        : null;

    /// <summary>
    /// A document window view model over fake files, a fake surface, a fake clock and a widths file in a temp folder.
    /// Disposing waits for every render and width change before it deletes the folder.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(bool persist = true)
        {
            WidthsFolder = Path.Combine(Path.GetTempPath(), "yamv-popover-widths-" + Guid.NewGuid().ToString("N"));
            WidthsFile = Path.Combine(WidthsFolder, "widths.json");
            Widths = new WidthStore(WidthsFile, Log);
            Reader = path => Files.TryGetValue(path, out string? text) ? text : throw new FileNotFoundException("No such fake file.", path);
            State = new DocumentWindowState(persist, FindRepo, path => !Absent.Contains(path), Plain);
            Timers = new TrackingTimeProvider(Time);
            ViewModel = new DocumentWindowViewModel(State, Surface, new DocumentWindowServices(Widths, path => Reader(path), Timers, Log));
        }

        /// <summary>The window's clock: <see cref="Time"/>, recording every timer it creates.</summary>
        public TrackingTimeProvider Timers { get; }

        public string WidthsFolder { get; }

        public string WidthsFile { get; }

        public WidthStore Widths { get; }

        public HashSet<string> Absent { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Files { get; } =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [DocA] = "# A",
                [DocB] = "# B",
                [PlanDoc] = "# v1 build",
            };

        public Func<string, string> Reader { get; set; }

        public FakeDocumentSurface Surface { get; } = new();

        public RecordingAppLog Log { get; } = new();

        public FakeTimeProvider Time { get; } = new();

        public DocumentWindowState State { get; }

        public DocumentWindowViewModel ViewModel { get; }

        /// <summary>Opens <paramref name="path"/>, waits for it to show, and creates its popover over a wide pane.</summary>
        public async Task<ContentWidthViewModel> OpenAsync(string path)
        {
            ViewModel.Open([path]);
            await ViewModel.WhenRendersSettled();
            return await CreateAsync();
        }

        public async Task<ContentWidthViewModel> CreateAsync(double paneWidth = WidePane)
        {
            ContentWidthViewModel? popover = await ViewModel.CreateContentWidthPopoverAsync(paneWidth);
            Assert.NotNull(popover);
            return popover;
        }

        /// <summary>Queues width work that waits until the returned source is completed, holding everything queued after it.</summary>
        public TaskCompletionSource HoldQueue()
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ViewModel.QueueWidthWork(() => hold.Task);
            return hold;
        }

        /// <summary>
        /// Queues width work that waits until <c>Hold</c> is completed, as <see cref="HoldQueue"/> does; <c>Reached</c> completes
        /// once that work has started, so everything queued before it has finished.
        /// </summary>
        public (TaskCompletionSource Hold, Task Reached) HoldQueueAndSignal()
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ViewModel.QueueWidthWork(() =>
            {
                reached.SetResult();
                return hold.Task;
            });
            return (hold, reached.Task);
        }

        /// <summary>Records the name of every property of the window's view model that changes from now on.</summary>
        public List<string?> RecordChanges()
        {
            List<string?> changes = [];
            ViewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            return changes;
        }

        public async ValueTask DisposeAsync()
        {
            ViewModel.Dispose();
            await ViewModel.WhenRendersSettled().WaitAsync(GateBackstop, TestContext.Current.CancellationToken);
            if (Directory.Exists(WidthsFolder))
            {
                Directory.Delete(WidthsFolder, recursive: true);
            }
        }
    }

    /// <summary>A clock that hands every call to <paramref name="inner"/> and records each timer it creates.</summary>
    private sealed class TrackingTimeProvider(TimeProvider inner) : TimeProvider
    {
        public List<TrackedTimer> Created { get; } = [];

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TrackedTimer(inner.CreateTimer(callback, state, dueTime, period));
            Created.Add(timer);
            return timer;
        }
    }

    /// <summary>A timer that records whether it was disposed.</summary>
    private sealed class TrackedTimer(ITimer inner) : ITimer
    {
        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

        public void Dispose()
        {
            IsDisposed = true;
            inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return inner.DisposeAsync();
        }
    }
}
