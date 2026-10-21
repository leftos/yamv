using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Widths;

namespace Yamv.App.Tests.Views;

/// <summary>The ⟷ title-bar button and the content-width popover it opens, in a shown main window.</summary>
public sealed class ContentWidthPopoverTests
{
    [Fact]
    public Task The_button_is_the_first_title_bar_tool_and_is_enabled_only_while_a_document_is_shown() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync();
            StackPanel tools = fixture.Window.WindowTitleBar.TitleBarTools;

            Assert.Same(fixture.Button, tools.Children[0]);
            Assert.False(fixture.Button.IsEnabled);

            fixture.ViewModel.Open([fixture.DocA]);
            await fixture.SettleAsync();
            Assert.True(fixture.Button.IsEnabled);

            fixture.ViewModel.Open([fixture.Gone]);
            await fixture.SettleAsync();
            Assert.False(fixture.Button.IsEnabled);
        });

    [Fact]
    public Task A_click_on_the_button_opens_the_popover_with_the_slider_focused() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);

            fixture.ClickEvent(fixture.Button);
            await fixture.SettleAsync();

            Assert.True(fixture.Window.WidthPopup.IsOpen);
            ContentWidthViewModel popover = Assert.IsType<ContentWidthViewModel>(fixture.ViewModel.ContentWidthPopover);
            Assert.Same(popover, fixture.Popover.DataContext);
            Assert.Contains("open", fixture.Button.Classes);
            Assert.True(fixture.Popover.WidthSlider.IsFocused);
        });

    [Fact]
    public Task A_pointer_click_on_the_button_while_the_popover_is_open_closes_it_and_focuses_the_document() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            ContentWidthViewModel popover = await fixture.OpenPopoverAsync(fixture.DocA);

            fixture.ClickIn(fixture.Button);
            await fixture.SettleAsync();

            fixture.AssertClosed(popover);
            Assert.True(fixture.Document.IsFocused);
        });

    [Fact]
    public Task The_window_losing_activation_closes_the_popover() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            ContentWidthViewModel popover = await fixture.OpenPopoverAsync(fixture.DocA);

            fixture.Deactivate();
            await fixture.SettleAsync();

            fixture.AssertClosed(popover);
        });

    [Fact]
    public Task Esc_closes_the_popover() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            ContentWidthViewModel popover = await fixture.OpenPopoverAsync(fixture.DocA);

            fixture.PressInPopover(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);
            await fixture.SettleAsync();

            fixture.AssertClosed(popover);
        });

    [Fact]
    public Task Showing_another_document_closes_the_popover() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            ContentWidthViewModel popover = await fixture.OpenPopoverAsync(fixture.DocA, fixture.DocB);

            fixture.ViewModel.SelectNext();
            await fixture.SettleAsync();

            fixture.AssertClosed(popover);
        });

    [Fact]
    public Task Closing_the_window_closes_the_popover() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            ContentWidthViewModel popover = await fixture.OpenPopoverAsync(fixture.DocA);

            fixture.Window.Close();
            await fixture.SettleAsync();

            Assert.False(fixture.Window.WidthPopup.IsOpen);
            Assert.True(popover.IsStale);
        });

    [Fact]
    public Task Command_and_right_bracket_typed_in_the_popover_steps_the_width_and_keeps_it_open() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(fixture.Docs, ContentWidth.FromPx(760)));
            fixture.Window.Width = 1600;
            ContentWidthViewModel popover = await fixture.OpenPopoverAsync(fixture.DocA);

            fixture.PressInPopover(Key.OemPlus, Fixture.CommandModifier, PhysicalKey.BracketRight);
            await fixture.SettleAsync();

            Assert.Equal(800, fixture.Widths.Get(fixture.Docs)?.Px);
            Assert.True(fixture.Window.WidthPopup.IsOpen);
            Assert.Equal("800 px", popover.Readout);
            Assert.Equal("800 px", fixture.Popover.Readout.Text);
        });

    [Fact]
    public Task The_segments_show_the_documents_folders_and_a_click_selects_one() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            ContentWidthViewModel popover = await fixture.OpenPopoverAsync(fixture.Plans);

            Assert.Equal(["docs/plans", "repo (repo)", "All documents"], fixture.SegmentLabels());
            Assert.Equal(WidthScopes.AllDocumentsLabel, popover.SelectedSegment?.Label);
            Assert.Equal(["on"], [.. fixture.SegmentButton("All documents").Classes.Where(name => name == "on")]);

            fixture.ClickIn(fixture.SegmentButton("repo (repo)"));
            await fixture.SettleAsync();

            Assert.Equal("repo (repo)", popover.SelectedSegment?.Label);
            Assert.Contains("on", fixture.SegmentButton("repo (repo)").Classes);
            Assert.DoesNotContain("on", fixture.SegmentButton("All documents").Classes);
            Assert.True(fixture.Popover.Inherited.IsVisible);
            Assert.Equal(WidthScopes.AllDocumentsLabel, fixture.Popover.InheritedFolder.Text);
        });

    [Fact]
    public Task Four_segments_each_show_their_whole_label() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(fixture.Docs, ContentWidth.FromPx(700)));
            fixture.Window.Width = 1600;
            await fixture.OpenPopoverAsync(fixture.Plans);
            // Headless text is about twice as wide as real text, so the card is twice as wide to hold the same labels.
            fixture.Popover.Card.Width = 600;
            await fixture.SettleAsync();

            Assert.Equal(["plans", "docs", "repo (repo)", "All documents"], fixture.SegmentLabels());
            foreach (string label in fixture.SegmentLabels())
            {
                Button segment = fixture.SegmentButton(label);
                TextBlock text = segment.GetVisualDescendants().OfType<TextBlock>().Single();
                text.Measure(Size.Infinity);
                Assert.True(
                    text.DesiredSize.Width <= segment.Bounds.Width,
                    $"'{label}' needs {text.DesiredSize.Width} px and its segment is {segment.Bounds.Width} px."
                );
            }
        });

    [Fact]
    public Task Three_segments_each_show_their_whole_label_when_one_needs_more_than_an_equal_third() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenPopoverAsync(fixture.Plans);
            // Headless text is about 11.5 px per character: "docs/plans" (10), "repo (repo)" (11) and "All documents" (13) need
            // 115, 126.5 and 149.5 px, 391 together, so they fit the 400 px left inside the card's 14 px padding at 428; an equal
            // third is 133.3 px, which cuts "All documents".
            fixture.Popover.Card.Width = 428;
            await fixture.SettleAsync();

            Assert.Equal(["docs/plans", "repo (repo)", "All documents"], fixture.SegmentLabels());
            foreach (string label in fixture.SegmentLabels())
            {
                Button segment = fixture.SegmentButton(label);
                TextBlock text = segment.GetVisualDescendants().OfType<TextBlock>().Single();
                text.Measure(Size.Infinity);
                Assert.True(
                    text.DesiredSize.Width <= segment.Bounds.Width,
                    $"'{label}' needs {text.DesiredSize.Width} px and its segment is {segment.Bounds.Width} px."
                );
            }
        });

    [Fact]
    public Task The_readout_shows_the_stored_width_and_Reset_removes_the_folders_own_width() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            string plans = Path.GetDirectoryName(fixture.Plans)!;
            Assert.True(fixture.Widths.Set(plans, ContentWidth.FromPx(760)));
            fixture.Window.Width = 1600;
            await fixture.OpenPopoverAsync(fixture.Plans);

            Assert.Equal("760 px", fixture.Popover.Readout.Text);
            Assert.False(fixture.Popover.Inherited.IsVisible);
            Assert.True(fixture.Popover.ResetLink.IsEffectivelyEnabled);

            fixture.ClickLink(fixture.Popover.ResetLink);
            await fixture.SettleAsync();

            Assert.Null(fixture.Widths.Get(plans));
            Assert.Equal("800 px", fixture.Popover.Readout.Text);
            Assert.False(fixture.Popover.ResetLink.IsEffectivelyEnabled);
        });

    [Fact]
    public Task A_pane_narrower_than_480_px_disables_the_slider_and_says_why() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Window.Width = 560;
            await fixture.OpenPopoverAsync(fixture.DocA);

            Assert.False(fixture.Popover.WidthSlider.IsEffectivelyEnabled);
            Assert.True(fixture.Popover.SliderCaption.IsVisible);
            Assert.Equal(ContentWidthViewModel.NarrowCaption, fixture.Popover.SliderCaption.Text);
            Assert.False(fixture.Popover.Ticks.IsVisible);
        });

    [Fact]
    public Task Arrow_keys_move_the_slider_on_the_grid_and_save_once_when_the_key_is_released() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(fixture.Docs, ContentWidth.FromPx(760)));
            fixture.Window.Width = 1600;
            ContentWidthViewModel popover = await fixture.OpenPopoverAsync(fixture.DocA);
            byte[] before = File.ReadAllBytes(fixture.WidthsFile);

            // A held key repeats its key down; only the release ends the drag.
            fixture.PressInPopover(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight);
            fixture.PressInPopover(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight);
            await fixture.SettleAsync();

            Assert.Equal("840 px", popover.Readout);
            Assert.Equal(before, File.ReadAllBytes(fixture.WidthsFile));

            fixture.ReleaseInPopover(Key.Right, PhysicalKey.ArrowRight);
            await fixture.SettleAsync();

            Assert.Equal(840, fixture.Widths.Get(fixture.Docs)?.Px);
            Assert.Equal<double?>([800, 840], fixture.Surface.ContentWidths.Distinct());
        });

    [Fact]
    public Task An_arrow_key_with_Shift_moves_the_slider_on_the_grid_too() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(fixture.Docs, ContentWidth.FromPx(760)));
            fixture.Window.Width = 1600;
            ContentWidthViewModel popover = await fixture.OpenPopoverAsync(fixture.DocA);

            fixture.PressInPopover(Key.Right, RawInputModifiers.Shift, PhysicalKey.ArrowRight);
            fixture.ReleaseInPopover(Key.Right, PhysicalKey.ArrowRight);
            await fixture.SettleAsync();

            Assert.Equal("800 px", popover.Readout);
            Assert.Equal(800, fixture.Widths.Get(fixture.Docs)?.Px);
        });

    [Fact]
    public Task Home_and_End_move_the_slider_to_480_and_Full() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Window.Width = 1600;
            ContentWidthViewModel popover = await fixture.OpenPopoverAsync(fixture.DocA);

            fixture.PressInPopover(Key.End, RawInputModifiers.None, PhysicalKey.End);
            Assert.Equal("Full", popover.Readout);
            fixture.PressInPopover(Key.Home, RawInputModifiers.None, PhysicalKey.Home);
            Assert.Equal("480 px", popover.Readout);
            fixture.ReleaseInPopover(Key.Home, PhysicalKey.Home);
            await fixture.SettleAsync();

            Assert.Equal(480, fixture.Widths.Default.Px);
        });

    [Fact]
    public Task A_save_that_fails_shows_the_foot_message() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(fixture.Docs, ContentWidth.FromPx(760)));
            fixture.Window.Width = 1600;
            await fixture.OpenPopoverAsync(fixture.DocA);
            Assert.False(fixture.Popover.Foot.IsVisible);

            using (new FileStream(fixture.WidthsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fixture.PressInPopover(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight);
                fixture.ReleaseInPopover(Key.Right, PhysicalKey.ArrowRight);
                await fixture.SettleAsync();
            }

            Assert.True(fixture.Popover.Foot.IsVisible);
            Assert.Equal(ContentWidthViewModel.SaveFailedMessage, fixture.Popover.FootText.Text);
            Assert.Equal("760 px", fixture.Popover.Readout.Text);
        });

    [Fact]
    public Task Clear_all_asks_first_and_clears_only_on_Clear() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            string plans = Path.GetDirectoryName(fixture.Plans)!;
            Assert.True(fixture.Widths.Set(plans, ContentWidth.FromPx(760)));
            Assert.True(fixture.Widths.Set(fixture.Docs, ContentWidth.FromPx(700)));
            fixture.Window.Width = 1600;
            await fixture.OpenPopoverAsync(fixture.Plans);
            ContentWidthPopover view = fixture.Popover;

            fixture.ClickLink(view.ClearAllLink);
            await fixture.SettleAsync();

            Assert.False(view.Links.IsVisible);
            Assert.True(view.ClearAllConfirm.IsVisible);
            Assert.Equal("Clear 2 saved widths under repo?", view.ClearAllQuestion.Text);
            Assert.Equal(760, fixture.Widths.Get(plans)?.Px);

            fixture.ClickLink(view.ClearAllYes);
            await fixture.SettleAsync();

            Assert.Null(fixture.Widths.Get(plans));
            Assert.Null(fixture.Widths.Get(fixture.Docs));
            Assert.True(view.Links.IsVisible);
            Assert.False(view.ClearAllConfirm.IsVisible);
            Assert.False(view.ClearAllLink.IsEffectivelyEnabled);
        });

    [Fact]
    public Task Cancel_and_Esc_put_the_Clear_all_link_back_without_clearing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(fixture.Docs, ContentWidth.FromPx(700)));
            await fixture.OpenPopoverAsync(fixture.DocA);
            ContentWidthPopover view = fixture.Popover;

            fixture.ClickLink(view.ClearAllLink);
            await fixture.SettleAsync();
            Assert.Equal("Clear 1 saved width under repo?", view.ClearAllQuestion.Text);
            fixture.ClickLink(view.ClearAllNo);
            await fixture.SettleAsync();

            Assert.True(view.Links.IsVisible);
            Assert.False(view.ClearAllConfirm.IsVisible);

            fixture.ClickLink(view.ClearAllLink);
            await fixture.SettleAsync();
            fixture.PressInPopover(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);
            await fixture.SettleAsync();

            Assert.True(fixture.Window.WidthPopup.IsOpen);
            Assert.True(view.Links.IsVisible);
            Assert.False(view.ClearAllConfirm.IsVisible);
            Assert.Equal(700, fixture.Widths.Get(fixture.Docs)?.Px);
        });

    [Fact]
    public Task Clear_all_on_a_narrow_pane_focuses_the_selected_segment_and_Esc_then_closes_the_popover() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(fixture.Docs, ContentWidth.FromPx(700)));
            fixture.Window.Width = 560;
            ContentWidthViewModel popover = await fixture.OpenPopoverAsync(fixture.DocA);
            ContentWidthPopover view = fixture.Popover;
            Assert.False(view.WidthSlider.IsEffectivelyEnabled);

            fixture.ClickLink(view.ClearAllLink);
            await fixture.SettleAsync();
            fixture.ClickLink(view.ClearAllYes);
            await fixture.SettleAsync();

            Button selected = fixture.SegmentButton(popover.SelectedSegment!.Label);
            Assert.True(
                selected.IsFocused,
                $"Focus is on {TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement()}; the link is "
                    + $"{(view.ClearAllLink.IsEffectivelyEnabled ? "enabled" : "disabled")} and the segment "
                    + $"{(selected.IsEffectivelyVisible ? "is" : "is not")} visible, focusable {selected.Focusable}."
            );

            fixture.PressInPopover(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);
            await fixture.SettleAsync();

            fixture.AssertClosed(popover);
        });

    [Fact]
    public Task Clear_all_on_a_wide_pane_focuses_the_selected_segment_and_Esc_then_closes_the_popover() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            Assert.True(fixture.Widths.Set(fixture.Docs, ContentWidth.FromPx(700)));
            fixture.Window.Width = 1600;
            ContentWidthViewModel popover = await fixture.OpenPopoverAsync(fixture.DocA);
            ContentWidthPopover view = fixture.Popover;
            Assert.True(view.WidthSlider.IsEffectivelyEnabled);

            fixture.ClickLink(view.ClearAllLink);
            await fixture.SettleAsync();
            fixture.ClickLink(view.ClearAllYes);
            await fixture.SettleAsync();

            Assert.True(fixture.SegmentButton(popover.SelectedSegment!.Label).IsFocused);

            fixture.PressInPopover(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape);
            await fixture.SettleAsync();

            fixture.AssertClosed(popover);
        });

    [Fact]
    public Task Clear_all_is_disabled_when_no_folder_holds_a_width() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenPopoverAsync(fixture.DocA);

            Assert.False(fixture.Popover.ClearAllLink.IsEffectivelyEnabled);
        });

    /// <summary>A shown main window over a temp repo with <c>docs/a.md</c>, <c>docs/b.md</c> and <c>docs/plans/v1.md</c>, and a missing <c>docs/gone.md</c>.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "yamv-popover-" + Guid.NewGuid().ToString("N"));
        private bool _closed;

        public Fixture()
        {
            string repo = Path.Combine(_root, "repo");
            Docs = Path.Combine(repo, "docs");
            DocA = Path.Combine(Docs, "a.md");
            DocB = Path.Combine(Docs, "b.md");
            Gone = Path.Combine(Docs, "gone.md");
            Plans = Path.Combine(Docs, "plans", "v1.md");
            Directory.CreateDirectory(Path.GetDirectoryName(Plans)!);
            File.WriteAllText(DocA, "# A");
            File.WriteAllText(DocB, "# B");
            File.WriteAllText(Plans, "# Plan");
            var state = new DocumentWindowState(
                true,
                path => path.StartsWith(repo, StringComparison.OrdinalIgnoreCase) ? repo : null,
                File.Exists,
                new GroupingOptions(null, StringComparison.OrdinalIgnoreCase)
            );
            WidthsFile = Path.Combine(_root, "data", "widths.json");
            Widths = new WidthStore(WidthsFile, Log);
            ViewModel = new DocumentWindowViewModel(
                state,
                Surface,
                new DocumentWindowServices(Widths, File.ReadAllText, new FakeTimeProvider(), Log)
            );
            Window = new MainWindow(ViewModel, Document, Surface, Log);
            Window.Closed += (_, _) => _closed = true;
            // The theme bridge that supplies the Yamv* brushes does not run here. Hovered links and segments and the open ⟷
            // paint these; without them their background is null and a pointer press goes through them.
            Window.Resources["YamvHoverBackground"] = Brushes.LightGray;
            Window.Resources["YamvSelectionBackground"] = Brushes.LightBlue;
        }

        public static RawInputModifiers CommandModifier => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        public string Docs { get; }

        public string DocA { get; }

        public string DocB { get; }

        public string Gone { get; }

        public string Plans { get; }

        public string WidthsFile { get; }

        public WidthStore Widths { get; }

        public FakeDocumentSurface Surface { get; } = new();

        /// <summary>The window's document view; focusable, as the web view is.</summary>
        public Border Document { get; } = new() { Focusable = true };

        public RecordingAppLog Log { get; } = new();

        public DocumentWindowViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public Button Button => Window.WindowTitleBar.ContentWidthButton;

        public ContentWidthPopover Popover => Window.WidthPopover;

        public async Task OpenAsync(params string[] paths)
        {
            if (paths.Length > 0)
            {
                ViewModel.Open(paths);
            }

            Window.Show();
            await SettleAsync();
        }

        /// <summary>Shows <paramref name="paths"/>, selecting the first, and opens the popover with a click on ⟷.</summary>
        public async Task<ContentWidthViewModel> OpenPopoverAsync(params string[] paths)
        {
            await OpenAsync(paths);
            ClickEvent(Button);
            await SettleAsync();
            Assert.True(Window.WidthPopup.IsOpen);
            return Assert.IsType<ContentWidthViewModel>(Popover.DataContext);
        }

        public void AssertClosed(ContentWidthViewModel popover)
        {
            Assert.False(Window.WidthPopup.IsOpen);
            Assert.True(popover.IsStale);
            Assert.Null(ViewModel.ContentWidthPopover);
            Assert.Null(Popover.DataContext);
            Assert.DoesNotContain("open", Button.Classes);
        }

        public string[] SegmentLabels() => [.. SegmentButtons().Select(button => (string)button.Content!)];

        public Button SegmentButton(string label) => SegmentButtons().Single(button => Equals(button.Content, label));

        /// <summary>Presses and releases the left button over the middle of <paramref name="control"/>, in whichever top level shows it.</summary>
        public void ClickIn(Control control) => ClickAt(control, new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));

        /// <summary>
        /// Clicks an enabled link with the pointer, 4 px in from its left edge: headless text is about twice as wide as real text,
        /// so a link's middle can lie past the card's edge.
        /// </summary>
        public void ClickLink(Button link)
        {
            Assert.True(link.IsEffectivelyEnabled);
            ClickAt(link, new Point(4, link.Bounds.Height / 2));
        }

        public void ClickEvent(Button button)
        {
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle();
        }

        /// <summary>A key down in the popover's own top level, where keyboard focus is while it is open.</summary>
        public void PressInPopover(Key key, RawInputModifiers modifiers, PhysicalKey physical)
        {
            PopoverTop().KeyPress(key, modifiers, physical, null);
            Settle();
        }

        public void ReleaseInPopover(Key key, PhysicalKey physical)
        {
            PopoverTop().KeyRelease(key, RawInputModifiers.None, physical, null);
            Settle();
        }

        public async Task SettleAsync()
        {
            Settle();
            await ViewModel.WhenRendersSettled();
            Settle();
        }

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            TopLevel.GetTopLevel(Popover)?.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        public async ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                Window.Close();
            }

            ViewModel.Dispose();
            await ViewModel.WhenRendersSettled();
            Directory.Delete(_root, recursive: true);
        }

        /// <summary>
        /// Raises the window's <c>Deactivated</c> as the platform does when another window takes activation. The headless platform
        /// raises it only when the window hides, and its callback is internal to Avalonia, so it is reached by reflection.
        /// </summary>
        public void Deactivate()
        {
            PropertyInfo callback =
                typeof(IWindowBaseImpl).GetProperty("Deactivated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("IWindowBaseImpl has no Deactivated callback.");
            Action deactivated =
                (Action?)callback.GetValue(Window.PlatformImpl) ?? throw new InvalidOperationException("Nothing listens for Deactivated.");
            deactivated();
        }

        /// <summary>Presses and releases the left button at <paramref name="point"/> in <paramref name="control"/>, in whichever top level shows it.</summary>
        private void ClickAt(Control control, Point point)
        {
            TopLevel top = TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("The control is not shown.");
            Point inTop = control.TranslatePoint(point, top) ?? throw new InvalidOperationException("The control is not in its top level.");
            top.MouseMove(inTop);
            Settle();
            top.MouseDown(inTop, MouseButton.Left);
            Settle();
            top.MouseUp(inTop, MouseButton.Left);
            Settle();
        }

        private TopLevel PopoverTop() => TopLevel.GetTopLevel(Popover) ?? throw new InvalidOperationException("The popover is not shown.");

        private List<Button> SegmentButtons() => [.. Popover.Segments.GetVisualDescendants().OfType<Button>()];
    }
}
