using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using Yamv.App.ViewModels;

namespace Yamv.App.Tests.ViewModels;

public sealed class DropOverlayViewModelTests
{
    [Fact]
    public Task A_page_signal_opens_it_undecided() =>
        Run(
            (overlay, _) =>
            {
                List<string?> changed = [];
                overlay.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

                overlay.PageDragOver();

                Assert.Equal(DropOverlayState.Undecided, overlay.State);
                Assert.Equal([nameof(DropOverlayViewModel.State)], changed);
            }
        );

    [Fact]
    public Task Entering_with_a_document_opens_it() =>
        Run(
            (overlay, _) =>
            {
                overlay.Entered(anyDocument: true);

                Assert.Equal(DropOverlayState.Open, overlay.State);
            }
        );

    [Fact]
    public Task Entering_with_no_document_refuses() =>
        Run(
            (overlay, _) =>
            {
                overlay.Entered(anyDocument: false);

                Assert.Equal(DropOverlayState.Refused, overlay.State);
            }
        );

    [Fact]
    public Task The_page_leaving_before_the_popup_enters_keeps_it_up() =>
        Run(
            (overlay, time) =>
            {
                overlay.PageDragOver();
                overlay.PageDragLeft();
                overlay.Entered(anyDocument: true);
                Advance(time, DropOverlayViewModel.Grace);

                Assert.Equal(DropOverlayState.Open, overlay.State);
            }
        );

    [Fact]
    public Task A_cancel_with_no_enter_hides_it_after_the_grace() =>
        Run(
            (overlay, time) =>
            {
                overlay.PageDragOver();
                Advance(time, DropOverlayViewModel.Grace - TimeSpan.FromMilliseconds(1));
                Assert.Equal(DropOverlayState.Undecided, overlay.State);

                Advance(time, TimeSpan.FromMilliseconds(1));

                Assert.Equal(DropOverlayState.Hidden, overlay.State);
            }
        );

    [Fact]
    public Task Moving_out_and_back_within_the_grace_keeps_it_up() =>
        Run(
            (overlay, time) =>
            {
                overlay.Entered(anyDocument: true);
                overlay.Left();
                Advance(time, DropOverlayViewModel.Grace - TimeSpan.FromMilliseconds(1));
                overlay.Entered(anyDocument: true);
                Advance(time, DropOverlayViewModel.Grace);

                Assert.Equal(DropOverlayState.Open, overlay.State);
            }
        );

    [Fact]
    public Task A_drop_hides_it_at_once() =>
        Run(
            (overlay, _) =>
            {
                overlay.Entered(anyDocument: true);

                overlay.Ended();

                Assert.Equal(DropOverlayState.Hidden, overlay.State);
            }
        );

    [Fact]
    public Task The_drag_signal_is_recent_only_inside_the_window() =>
        Run(
            (overlay, time) =>
            {
                Assert.False(overlay.DragSignalledRecently);

                overlay.PageDragOver();
                Assert.True(overlay.DragSignalledRecently);

                Advance(time, DropOverlayViewModel.FallbackWindow);
                Assert.True(overlay.DragSignalledRecently);

                Advance(time, TimeSpan.FromMilliseconds(1));
                Assert.False(overlay.DragSignalledRecently);
            }
        );

    [Fact]
    public Task The_page_leaving_ends_the_signal() =>
        Run(
            (overlay, time) =>
            {
                overlay.PageDragOver();

                overlay.PageDragLeft();

                Assert.False(overlay.DragSignalledRecently);
                Advance(time, DropOverlayViewModel.Grace);
                Assert.Equal(DropOverlayState.Hidden, overlay.State);
            }
        );

    [Fact]
    public Task Ending_ends_the_signal() =>
        Run(
            (overlay, _) =>
            {
                overlay.PageDragOver();

                overlay.Ended();

                Assert.False(overlay.DragSignalledRecently);
            }
        );

    private static Task Run(Action<DropOverlayViewModel, FakeTimeProvider> test) =>
        Headless.RunAsync(() =>
        {
            var time = new FakeTimeProvider();
            using var overlay = new DropOverlayViewModel(time);
            test(overlay, time);
            return Task.CompletedTask;
        });

    private static void Advance(FakeTimeProvider time, TimeSpan by)
    {
        time.Advance(by);
        Dispatcher.UIThread.RunJobs();
    }
}
