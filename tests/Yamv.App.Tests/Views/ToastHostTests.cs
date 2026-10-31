using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Widths;

namespace Yamv.App.Tests.Views;

/// <summary>The toasts of a shown main window: their popup at the foot of the document pane and the cards in it.</summary>
public sealed class ToastHostTests
{
    [Fact]
    public Task A_toast_opens_the_popup_at_the_foot_of_the_document_pane_until_it_fades() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ShowAsync();
            Assert.False(fixture.Window.ToastPopup.IsOpen);

            fixture.Toasts.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            fixture.Settle();

            Popup popup = fixture.Window.ToastPopup;
            Assert.True(popup.IsOpen);
            Assert.Same(fixture.Window.DocumentColumn, popup.PlacementTarget);
            Assert.Equal(
                (PlacementMode.AnchorAndGravity, PopupAnchor.Bottom, PopupGravity.Top, false, false),
                (popup.Placement, popup.PlacementAnchor, popup.PlacementGravity, popup.IsLightDismissEnabled, popup.ShouldUseOverlayLayer)
            );
            Assert.Equal("Couldn't save the width; see the log", TextOf(Assert.Single(fixture.Cards())));
            Assert.True(fixture.Toasts.IsActive);

            fixture.Time.Advance(TimeSpan.FromMilliseconds(4950));
            fixture.Settle();
            Assert.True(popup.IsOpen);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(50));
            fixture.Settle();
            Assert.False(popup.IsOpen);
        });

    [Fact]
    public Task The_close_button_takes_its_card_down_and_the_last_one_closes_the_popup() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ShowAsync();
            fixture.Toasts.Show(ToastKind.Warning, "first");
            fixture.Toasts.Show(ToastKind.Refusal, "second");
            fixture.Settle();
            Assert.Equal(2, fixture.Cards().Count);

            fixture.ClickClose(fixture.Cards()[0]);
            Assert.Equal("second", TextOf(Assert.Single(fixture.Cards())));
            Assert.True(fixture.Window.ToastPopup.IsOpen);

            fixture.ClickClose(fixture.Cards()[0]);
            Assert.False(fixture.Window.ToastPopup.IsOpen);
        });

    [Fact]
    public Task A_repeated_toast_shows_its_count_on_its_card() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ShowAsync();
            fixture.Toasts.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            fixture.Settle();
            Border count = Fixture.Part<Border>(Assert.Single(fixture.Cards()), "count");
            Assert.False(count.IsEffectivelyVisible);

            fixture.Toasts.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            fixture.Toasts.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            fixture.Settle();

            Assert.Single(fixture.Cards());
            Assert.True(count.IsEffectivelyVisible);
            Assert.Equal("×3", Assert.IsType<TextBlock>(count.Child).Text);
        });

    [Fact]
    public Task Text_between_backticks_shows_as_inline_code() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ShowAsync();

            fixture.Toasts.Show(ToastKind.Refusal, "`notes/diagram.png` doesn't exist");
            fixture.Settle();

            Border card = Assert.Single(fixture.Cards());
            Border code = Fixture.Part<Border>(card, "code");
            Assert.Equal("notes/diagram.png", Assert.IsType<TextBlock>(code.Child).Text);
            Assert.Same(fixture.CodeBackground, code.Background);
            Assert.Equal("notes/diagram.png doesn't exist", TextOf(card));
        });

    [Fact]
    public Task Showing_a_toast_asks_for_no_focus_and_its_card_takes_none() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ShowAsync();
            fixture.Document.Focus();
            fixture.Settle();
            Assert.True(fixture.Document.IsFocused);

            fixture.Toasts.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            fixture.Settle();

            // Win32 shows a popup without activating it, while the headless platform activates every top level it shows, so
            // the document's focus is not asserted after the show: the popup must not ask for focus, nor hold any.
            Assert.False(fixture.Window.ToastPopup.TakesFocusFromNativeControl);
            Assert.True(fixture.Window.IsActive);
            Button close = Fixture.Part<Button>(Assert.Single(fixture.Cards()), "toastClose");
            Assert.False(close.Focusable);
            Assert.Null(TopLevel.GetTopLevel(close)?.FocusManager?.GetFocusedElement());
        });

    [Fact]
    public Task The_card_follows_a_theme_change() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ShowAsync();
            fixture.Window.RequestedThemeVariant = ThemeVariant.Light;
            fixture.Toasts.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            fixture.Settle();
            Border card = Assert.Single(fixture.Cards());
            Assert.Same(Brushes.White, card.Background);
            AssertShadows(card, Color.Parse("#1F2328"), 0.18);

            fixture.Window.Resources["YamvPageBackground"] = Brushes.Black;
            fixture.Window.RequestedThemeVariant = ThemeVariant.Dark;
            fixture.Settle();

            Assert.Same(Brushes.Black, card.Background);
            AssertShadows(card, Colors.Black, 0.6);
        });

    [Fact]
    public Task The_fade_holds_while_the_window_is_minimized() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ShowAsync();
            fixture.Toasts.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            fixture.Settle();
            fixture.Time.Advance(TimeSpan.FromSeconds(2));

            fixture.Window.WindowState = WindowState.Minimized;
            fixture.Settle();
            Assert.False(fixture.Toasts.IsActive);
            fixture.Time.Advance(TimeSpan.FromSeconds(30));
            Assert.Single(fixture.Toasts.Items);

            fixture.Window.WindowState = WindowState.Normal;
            fixture.Settle();
            Assert.True(fixture.Toasts.IsActive);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(2950));
            Assert.Single(fixture.Toasts.Items);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(50));
            fixture.Settle();
            Assert.Empty(fixture.Toasts.Items);
            Assert.False(fixture.Window.ToastPopup.IsOpen);
        });

    [Fact]
    public Task A_minimized_window_closes_its_toast_popup_and_restoring_it_opens_it_again() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ShowAsync();
            fixture.Toasts.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            fixture.Settle();
            Assert.True(fixture.Window.ToastPopup.IsOpen);

            fixture.Window.WindowState = WindowState.Minimized;
            fixture.Settle();
            Assert.False(fixture.Window.ToastPopup.IsOpen);
            Assert.Single(fixture.Toasts.Items);

            fixture.Window.WindowState = WindowState.Normal;
            fixture.Settle();
            Assert.True(fixture.Window.ToastPopup.IsOpen);
            Assert.Single(fixture.Cards());
        });

    [Fact]
    public Task Only_the_cards_take_a_hit_not_the_margin_the_gap_or_the_space_beside_a_narrower_card() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.ShowAsync();
            fixture.Toasts.Show(ToastKind.Warning, "Couldn't save the width; see the log");
            fixture.Toasts.Show(ToastKind.Warning, "short");
            fixture.Settle();
            ToastHost host = fixture.Window.ToastHost;
            List<Border> cards = fixture.Cards();
            Border wide = cards[0];
            Border narrow = cards[1];
            Assert.True(narrow.Bounds.Width < wide.Bounds.Width);
            Point At(Border card, double x, double y) => card.TranslatePoint(new Point(x, y), host)!.Value;

            Assert.True(host.HasCardAt(At(wide, wide.Bounds.Width / 2, wide.Bounds.Height / 2)));
            Assert.True(host.HasCardAt(At(narrow, 1, 1)));
            Assert.False(host.HasCardAt(new Point(2, 2)));
            Assert.False(host.HasCardAt(At(wide, wide.Bounds.Width / 2, wide.Bounds.Height + 4)));
            Assert.False(host.HasCardAt(At(narrow, -4, narrow.Bounds.Height / 2)));
        });

    [Fact]
    public Task A_long_toast_stays_inside_the_document_pane() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Window.Width = 560;
            await fixture.ShowAsync();

            fixture.Toasts.Show(ToastKind.Warning, new string('m', 300));
            fixture.Settle();

            Border card = Assert.Single(fixture.Cards());
            ToastText text = card.GetVisualDescendants().OfType<ToastText>().Single();
            Assert.True(card.Bounds.Width <= fixture.Window.DocumentColumn.Bounds.Width);
            TextLine line = Assert.Single(text.TextLayout.TextLines);
            Assert.True(line.HasCollapsed);
        });

    [Fact]
    public Task A_long_code_value_keeps_its_box_inside_the_card() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Window.Width = 560;
            await fixture.ShowAsync();

            fixture.Toasts.Show(ToastKind.Warning, $"{ToastValue.Code(new string('m', 64))} no longer exists");
            fixture.Settle();

            Border card = Assert.Single(fixture.Cards());
            ToastText text = card.GetVisualDescendants().OfType<ToastText>().Single();
            Border box = Fixture.Part<Border>(card, "code");
            TextBlock name = Assert.IsType<TextBlock>(box.Child);
            Assert.True(name.Bounds.Width > 0, "the code box kept no text");
            Assert.True(name.Bounds.Width <= name.MaxWidth, "the code box passed its cap");
            Assert.True(card.Bounds.Width <= fixture.Window.DocumentColumn.Bounds.Width);
            Assert.True(text.Bounds.Width > name.Bounds.Width, "the code box took the whole line");
            TextLine line = Assert.Single(name.TextLayout.TextLines);
            Assert.True(line.HasCollapsed);
        });

    [Fact]
    public Task A_long_code_value_after_plain_text_keeps_its_box() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Window.Width = 1000;
            await fixture.ShowAsync();

            fixture.Toasts.Show(ToastKind.Warning, $"YAMV doesn't open links outside {ToastValue.Code(new string('m', 64))}");
            fixture.Settle();

            Border card = Assert.Single(fixture.Cards());
            ToastText text = card.GetVisualDescendants().OfType<ToastText>().Single();
            Border box = Fixture.Part<Border>(card, "code");
            TextBlock name = Assert.IsType<TextBlock>(box.Child);
            Assert.True(name.Bounds.Width > 0, "the code box kept no text");
            Assert.True(Assert.Single(name.TextLayout.TextLines).HasCollapsed, "the code box did not elide");
            Assert.True(box.Bounds.Width > 0, "the code box took no room");
            Assert.True(box.Bounds.Right <= text.Bounds.Width, "the code box ran past the line");
        });

    [Fact]
    public Task The_text_after_a_long_code_value_stays_on_the_line() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Window.Width = 1000;
            await fixture.ShowAsync();

            fixture.Toasts.Show(ToastKind.Warning, $"Couldn't show {ToastValue.Code(new string('m', 64))}; see the log");
            fixture.Settle();

            Border card = Assert.Single(fixture.Cards());
            ToastText text = card.GetVisualDescendants().OfType<ToastText>().Single();
            Border box = Fixture.Part<Border>(card, "code");
            TextBlock name = Assert.IsType<TextBlock>(box.Child);
            Assert.True(name.Bounds.Width > 0, "the code box kept no text");
            Assert.True(Assert.Single(name.TextLayout.TextLines).HasCollapsed, "the code box did not elide");
            Assert.True(box.Bounds.Width > 0, "the code box took no room");
            Assert.True(box.Bounds.Right <= text.Bounds.Width, "the code box ran past the line");
            Assert.False(Assert.Single(text.TextLayout.TextLines).HasCollapsed, "the line elided its own text");
        });

    /// <summary>The card's text as it reads: its plain runs and its inline code, in order.</summary>
    private static string TextOf(Border card)
    {
        ToastText text = card.GetVisualDescendants().OfType<ToastText>().Single();
        return string.Concat(
            text.Inlines!.Select(inline =>
                inline switch
                {
                    Run run => run.Text,
                    InlineUIContainer { Child: Border { Child: TextBlock code } } => code.Text,
                    _ => throw new InvalidOperationException($"Unexpected inline {inline.GetType().Name}."),
                }
            )
        );
    }

    /// <summary>Asserts the card casts the toast's far (0 10 22) and near (0 1 3) shadows in <paramref name="color"/>.</summary>
    private static void AssertShadows(Border card, Color color, double opacity)
    {
        DropShadowEffect far = Assert.IsType<DropShadowEffect>(card.Effect);
        DropShadowEffect near = Assert.IsType<DropShadowEffect>(Assert.IsAssignableFrom<Visual>(card.GetVisualParent()).Effect);
        Assert.Equal((color, opacity, 0d, 10d, 22d), (far.Color, far.Opacity, far.OffsetX, far.OffsetY, far.BlurRadius));
        Assert.Equal((color, opacity, 0d, 1d, 3d), (near.Color, near.Opacity, near.OffsetX, near.OffsetY, near.BlurRadius));
    }

    /// <summary>A shown main window with no documents, whose toasts run on a fake clock.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private bool _closed;

        public Fixture()
        {
            string data = Path.Combine(Path.GetTempPath(), "yamv-toasts-" + Guid.NewGuid().ToString("N"));
            var state = new DocumentWindowState(true, _ => null, _ => true, new GroupingOptions(null, StringComparison.OrdinalIgnoreCase));
            Toasts = new ToastCenter(Time);
            ViewModel = new DocumentWindowViewModel(
                state,
                Surface,
                new DocumentWindowServices(new WidthStore(Path.Combine(data, "widths.json"), Log), _ => "# A", Time, Log, Toasts: Toasts)
                {
                    Elevated = false,
                }
            );
            Window = new MainWindow(ViewModel, Document, Surface, Log);
            Window.Closed += (_, _) => _closed = true;
            // The theme bridge that supplies the Yamv* brushes does not run here.
            Window.Resources["YamvPageBackground"] = Brushes.White;
            Window.Resources["YamvCodeBackground"] = CodeBackground;
        }

        public FakeTimeProvider Time { get; } = new();

        public ToastCenter Toasts { get; }

        public SolidColorBrush CodeBackground { get; } = new(Colors.Gainsboro);

        public FakeDocumentSurface Surface { get; } = new();

        /// <summary>The window's document view; focusable, as the web view is.</summary>
        public Border Document { get; } = new() { Focusable = true };

        public RecordingAppLog Log { get; } = new();

        public DocumentWindowViewModel ViewModel { get; }

        public MainWindow Window { get; }

        /// <summary>Shows the window with one document, which the fake reader reads as a heading.</summary>
        public async Task ShowAsync()
        {
            ViewModel.Open([Path.Combine(Path.GetTempPath(), "yamv-toasts", "a.md")]);
            Window.Show();
            Settle();
            await ViewModel.WhenRendersSettled();
            Settle();
        }

        /// <summary>The cards shown, oldest first.</summary>
        public List<Border> Cards() => [.. Window.ToastHost.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("card"))];

        /// <summary>The one control of type <typeparamref name="T"/> with class <paramref name="className"/> in <paramref name="card"/>.</summary>
        public static T Part<T>(Border card, string className)
            where T : Control => card.GetVisualDescendants().OfType<T>().Single(control => control.Classes.Contains(className));

        public void ClickClose(Border card)
        {
            Part<Button>(card, "toastClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle();
        }

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.ToastHost.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public async ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                Window.Close();
            }

            await ViewModel.WhenRendersSettled();
            ViewModel.Dispose();
        }
    }
}
