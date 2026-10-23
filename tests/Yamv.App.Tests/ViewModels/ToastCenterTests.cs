using Microsoft.Extensions.Time.Testing;
using Yamv.App.Services;
using Yamv.App.ViewModels;

namespace Yamv.App.Tests.ViewModels;

/// <summary>A window's toasts: the cards shown, their 5 s fade counted only while the window is active, the cap and repeats.</summary>
public sealed class ToastCenterTests
{
    [Fact]
    public Task Show_adds_a_card_with_its_kind_and_text() =>
        Headless.Run(() =>
        {
            using var center = new ToastCenter(new FakeTimeProvider());

            center.Show(ToastKind.Refusal, "YAMV doesn't open `javascript:` links");

            ToastItem item = Assert.Single(center.Items);
            Assert.Equal((ToastKind.Refusal, "YAMV doesn't open `javascript:` links"), (item.Kind, item.Text));
            Assert.Equal(1, item.Count);
            Assert.False(item.HasCount);
            Assert.Equal(1, item.TimeLeft);
        });

    [Fact]
    public Task A_card_fades_after_5_s_of_active_time() =>
        Headless.Run(() =>
        {
            var time = new FakeTimeProvider();
            using var center = new ToastCenter(time);
            center.Show(ToastKind.Warning, "Couldn't save the width; see the log");

            time.Advance(TimeSpan.FromMilliseconds(2500));
            Assert.Equal(0.5, Assert.Single(center.Items).TimeLeft, 6);
            time.Advance(TimeSpan.FromMilliseconds(2400));
            Assert.Single(center.Items);
            time.Advance(TimeSpan.FromMilliseconds(100));

            Assert.Empty(center.Items);
        });

    [Fact]
    public Task Time_while_the_window_is_inactive_does_not_count() =>
        Headless.Run(() =>
        {
            var time = new FakeTimeProvider();
            using var center = new ToastCenter(time);
            center.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            time.Advance(TimeSpan.FromSeconds(2));

            center.IsActive = false;
            time.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(0.6, Assert.Single(center.Items).TimeLeft, 6);

            center.IsActive = true;
            time.Advance(TimeSpan.FromMilliseconds(2900));
            Assert.Single(center.Items);
            time.Advance(TimeSpan.FromMilliseconds(100));
            Assert.Empty(center.Items);
        });

    [Fact]
    public Task A_card_shown_while_the_window_is_inactive_waits_for_it() =>
        Headless.Run(() =>
        {
            var time = new FakeTimeProvider();
            using var center = new ToastCenter(time) { IsActive = false };
            center.Show(ToastKind.Warning, "Couldn't save the width; see the log");

            time.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(1, Assert.Single(center.Items).TimeLeft);

            center.IsActive = true;
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.Empty(center.Items);
        });

    [Fact]
    public Task Dismiss_removes_the_card() =>
        Headless.Run(() =>
        {
            using var center = new ToastCenter(new FakeTimeProvider());
            center.Show(ToastKind.Warning, "first");
            center.Show(ToastKind.Warning, "second");

            center.Dismiss(center.Items[0]);

            Assert.Equal(["second"], center.Items.Select(item => item.Text));
        });

    [Fact]
    public Task A_fourth_card_evicts_the_oldest() =>
        Headless.Run(() =>
        {
            using var center = new ToastCenter(new FakeTimeProvider());

            center.Show(ToastKind.Warning, "one");
            center.Show(ToastKind.Warning, "two");
            center.Show(ToastKind.Warning, "three");
            center.Show(ToastKind.Warning, "four");

            Assert.Equal(["two", "three", "four"], center.Items.Select(item => item.Text));
        });

    [Fact]
    public Task The_same_text_while_its_card_is_up_counts_and_restarts_the_fade() =>
        Headless.Run(() =>
        {
            var time = new FakeTimeProvider();
            using var center = new ToastCenter(time);
            center.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            center.Show(ToastKind.Warning, "other");
            time.Advance(TimeSpan.FromSeconds(3));

            center.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            center.Show(ToastKind.Warning, "Couldn't save the width; see the log");

            Assert.Equal(["Couldn't save the width; see the log", "other"], center.Items.Select(item => item.Text));
            ToastItem repeated = center.Items[0];
            Assert.Equal((3, true, "×3", 1d), (repeated.Count, repeated.HasCount, repeated.CountText, repeated.TimeLeft));
            time.Advance(TimeSpan.FromMilliseconds(2000));
            Assert.Same(repeated, Assert.Single(center.Items));
            time.Advance(TimeSpan.FromMilliseconds(2900));
            Assert.Same(repeated, Assert.Single(center.Items));
            time.Advance(TimeSpan.FromMilliseconds(100));
            Assert.Empty(center.Items);
        });

    [Fact]
    public Task The_same_text_after_its_card_faded_gets_a_new_card_without_a_count() =>
        Headless.Run(() =>
        {
            var time = new FakeTimeProvider();
            using var center = new ToastCenter(time);
            center.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            time.Advance(TimeSpan.FromSeconds(5));

            center.Show(ToastKind.Warning, "Couldn't save the width; see the log");

            Assert.Equal(1, Assert.Single(center.Items).Count);
        });

    [Fact]
    public Task Backticks_mark_inline_code_and_an_unmatched_one_shows_as_it_is() =>
        Headless.Run(() =>
        {
            using var center = new ToastCenter(new FakeTimeProvider());
            center.Show(ToastKind.Refusal, "`notes/diagram.png` doesn't exist");
            center.Show(ToastKind.Warning, "a `b` c `d");

            Assert.Equal(
                [new ToastSegment("notes/diagram.png", IsCode: true), new ToastSegment(" doesn't exist", IsCode: false)],
                center.Items[0].Segments
            );
            Assert.Equal(
                [new ToastSegment("a ", IsCode: false), new ToastSegment("b", IsCode: true), new ToastSegment(" c `d", IsCode: false)],
                center.Items[1].Segments
            );
        });
}
