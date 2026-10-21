using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using Yamv.App.Controls;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Session;

namespace Yamv.App.Tests.Views;

public sealed class MainWindowTests
{
    [Fact]
    public Task Tree_shows_repo_group_badge_folder_and_documents() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            List<string> texts = VisibleTexts(fixture.Window.Tree);

            Assert.Contains("repo", texts);
            Assert.Contains("GIT", texts);
            Assert.DoesNotContain("DIR", texts);
            Assert.Contains("docs", texts);
            Assert.Contains("a.md", texts);
            Assert.Contains("b.md", texts);
        });

    [Fact]
    public Task Missing_document_row_shows_missing_tag() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.Gone);

            Panel row = fixture.Row(fixture.Gone);

            Assert.Contains("MISSING", VisibleTexts(row));
            Assert.DoesNotContain("MISSING", VisibleTexts(fixture.Row(fixture.DocA)));
        });

    [Fact]
    public Task Close_button_closes_its_document_without_selecting_it_first() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            List<string?> selections = [];
            fixture.ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DocumentWindowViewModel.SelectedPath))
                {
                    selections.Add(fixture.ViewModel.SelectedPath);
                }
            };

            fixture.Window.MouseMove(fixture.Center(fixture.Row(fixture.DocB)));
            fixture.Settle();
            Button close = fixture.Row(fixture.DocB).GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("close"));
            Assert.True(close.IsEffectivelyVisible);
            Assert.Equal("Close b.md", AutomationProperties.GetName(close));
            fixture.Click(close);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.DoesNotContain(fixture.DocB, fixture.State.Documents);
            Assert.Equal(fixture.DocA, fixture.ViewModel.SelectedPath);
            Assert.DoesNotContain(fixture.DocB, selections);
            Assert.DoesNotContain(fixture.Surface.Shown, shown => shown.DocumentPath == fixture.DocB);
        });

    [Fact]
    public Task Clicking_a_row_then_down_and_up_moves_selection_and_keeps_focus_in_tree() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocB, fixture.DocA);

            IReadOnlyList<TreeItemViewModel> items = fixture.ViewModel.Items;

            fixture.Click(fixture.Row(fixture.DocA));
            await fixture.ViewModel.WhenRendersSettled();
            Assert.Equal(fixture.DocA, fixture.ViewModel.SelectedPath);
            fixture.AssertFocusOnSelectedRow();

            fixture.Press(Key.Down);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
            fixture.AssertFocusOnSelectedRow();

            fixture.Press(Key.Up);
            Assert.Equal(fixture.DocA, fixture.ViewModel.SelectedPath);
            fixture.AssertFocusOnSelectedRow();
            Assert.Same(items, fixture.ViewModel.Items);
        });

    [Fact]
    public Task Down_and_up_step_across_a_folder_header_in_one_press() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.Design, fixture.Plans);

            fixture.Click(fixture.Row(fixture.Design));
            await fixture.ViewModel.WhenRendersSettled();
            Assert.Equal(fixture.Design, fixture.ViewModel.SelectedPath);

            fixture.Press(Key.Down);
            Assert.Equal(fixture.Plans, fixture.ViewModel.SelectedPath);
            fixture.AssertFocusOnSelectedRow();

            fixture.Press(Key.Up);
            Assert.Equal(fixture.Design, fixture.ViewModel.SelectedPath);
            fixture.AssertFocusOnSelectedRow();
        });

    [Fact]
    public Task Selecting_a_document_in_a_collapsed_folder_expands_the_folder() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.Design, fixture.Plans);
            FolderItemViewModel plans = AllRows(fixture.ViewModel.Items).OfType<FolderItemViewModel>().Single(row => row.Name == "plans");
            plans.IsExpanded = false;
            fixture.Settle();

            fixture.ViewModel.SelectNext();
            fixture.Settle();

            Assert.Equal(fixture.Plans, fixture.ViewModel.SelectedPath);
            Assert.True(plans.IsExpanded);
            Assert.True(fixture.Row(fixture.Plans).IsEffectivelyVisible);
        });

    [Fact]
    public Task Window_with_no_documents_shows_empty_state_and_hides_document_view_and_sidebar() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync();

            Assert.True(fixture.Window.EmptyState.IsVisible);
            Assert.False(fixture.Window.DocumentHost.IsVisible);
            Assert.False(fixture.Window.MissingPane.IsVisible);
            Assert.Contains("No documents open", VisibleTexts(fixture.Window.EmptyState));
            Assert.False(fixture.Window.SidebarSplitter.IsVisible);
            Assert.False(fixture.Window.Sidebar.IsVisible);
            Assert.Equal(0, fixture.Window.Body.ColumnDefinitions[2].ActualWidth);
        });

    [Fact]
    public Task Selected_missing_document_shows_missing_pane_and_hides_document_view() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.Gone, fixture.DocA);

            Assert.Equal(fixture.Gone, fixture.ViewModel.SelectedPath);
            Assert.True(fixture.Window.MissingPane.IsVisible);
            Assert.False(fixture.Window.DocumentHost.IsVisible);
            Assert.False(fixture.Window.EmptyState.IsVisible);
            Assert.Equal(fixture.Gone, fixture.Window.MissingPane.MissingPath.Text);
            Assert.Contains("It was moved or deleted:", VisibleTexts(fixture.Window.MissingPane));
        });

    [Fact]
    public Task Viewer_with_one_document_hides_splitter_and_sidebar() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture(persist: false);
            await fixture.OpenAsync(fixture.DocA);

            Assert.False(fixture.Window.SidebarSplitter.IsVisible);
            Assert.False(fixture.Window.Sidebar.IsVisible);
            Assert.Equal(0, fixture.Window.Body.ColumnDefinitions[2].ActualWidth);
            Assert.True(fixture.Window.DocumentHost.IsVisible);
        });

    [Fact]
    public Task Command_W_closes_the_selected_document() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            Assert.Equal(fixture.DocA, fixture.ViewModel.SelectedPath);

            fixture.Press(Key.W, CommandModifier);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.DoesNotContain(fixture.DocA, fixture.State.Documents);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Ctrl_tab_and_ctrl_shift_tab_step_selection_and_keep_focus_on_the_selected_row() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            fixture.Click(fixture.Row(fixture.DocA));
            await fixture.ViewModel.WhenRendersSettled();

            fixture.Press(Key.Tab, RawInputModifiers.Control);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
            fixture.AssertFocusOnSelectedRow();

            fixture.Press(Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.Equal(fixture.DocA, fixture.ViewModel.SelectedPath);
            fixture.AssertFocusOnSelectedRow();
        });

    [Fact]
    public Task Command_W_with_focus_in_the_tree_keeps_focus_on_the_new_selected_row() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB, fixture.Gone);
            fixture.Click(fixture.Row(fixture.DocA));
            await fixture.ViewModel.WhenRendersSettled();
            fixture.AssertFocusOnSelectedRow();

            fixture.Press(Key.W, CommandModifier);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Settle();
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
            fixture.AssertFocusOnSelectedRow();

            fixture.Press(Key.Down);
            Assert.Equal(fixture.Gone, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Saved_bounds_keep_the_normal_rectangle_while_maximized() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.Window.Position = new PixelPoint(120, 80);
            fixture.Window.Width = 900;
            fixture.Window.Height = 700;
            await fixture.OpenAsync(fixture.DocA);
            WindowBounds normal = Assert.IsType<WindowBounds>(fixture.Window.SavedBounds);
            Assert.False(normal.IsMaximized);

            fixture.Window.WindowState = WindowState.Maximized;
            fixture.Window.Position = new PixelPoint(0, 0);
            fixture.Window.Width = 1600;
            fixture.Settle();

            Assert.Equal(normal with { IsMaximized = true }, fixture.Window.SavedBounds);

            fixture.Window.WindowState = WindowState.Normal;
            fixture.Settle();
            Assert.False(fixture.Window.SavedBounds!.IsMaximized);
        });

    [Fact]
    public Task Command_B_toggles_the_sidebar() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            fixture.Press(Key.B, CommandModifier);
            Assert.True(fixture.ViewModel.SidebarHidden);
            Assert.False(fixture.Window.Sidebar.IsVisible);

            fixture.Press(Key.B, CommandModifier);
            Assert.False(fixture.ViewModel.SidebarHidden);
            Assert.True(fixture.Window.Sidebar.IsVisible);
        });

    [Fact]
    public Task Command_R_renders_the_selected_document_again() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            int before = fixture.Surface.Shown.Count;

            fixture.Press(Key.R, CommandModifier);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Settle();

            Assert.Equal(before + 1, fixture.Surface.Shown.Count);
            Assert.Equal(fixture.DocA, fixture.Surface.Shown[^1].DocumentPath);
        });

    [Fact]
    public Task Forwarded_command_w_from_the_page_closes_the_selected_document() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            bool mac = OperatingSystem.IsMacOS();

            fixture.Surface.RaiseKey(new KeyForwardedEventArgs("w", ctrl: !mac, meta: mac, shift: false, alt: false));
            await fixture.ViewModel.WhenRendersSettled();

            Assert.DoesNotContain(fixture.DocA, fixture.State.Documents);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Command_W_on_a_viewer_with_one_document_closes_the_window() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture(persist: false);
            await fixture.OpenAsync(fixture.DocA);

            fixture.Press(Key.W, CommandModifier);

            Assert.True(fixture.IsClosed);
        });

    [Fact]
    public Task Sidebar_width_above_the_limit_reads_480() =>
        Headless.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            fixture.Window.SidebarWidth = 600;

            Assert.Equal(480, fixture.Window.SidebarWidth);
        });

    /// <summary>The platform's command key: Cmd on macOS, Ctrl elsewhere.</summary>
    private static RawInputModifiers CommandModifier => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static List<string> VisibleTexts(Visual root) =>
        [
            .. root.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && text.Text is not null)
                .Select(text => text.Text!),
        ];

    private static IEnumerable<TreeItemViewModel> AllRows(IEnumerable<TreeItemViewModel> rows) =>
        rows.SelectMany(row =>
            row switch
            {
                GroupItemViewModel group => AllRows(group.Children).Prepend(row),
                FolderItemViewModel folder => AllRows(folder.Children).Prepend(row),
                _ => [row],
            }
        );

    /// <summary>
    /// A shown main window over temp files: a repo with <c>docs/a.md</c>, <c>docs/b.md</c>, <c>docs/design/look.md</c> and
    /// <c>docs/plans/v1.md</c>, and a missing <c>docs/gone.md</c>.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "yamv-window-" + Guid.NewGuid().ToString("N"));

        public Fixture(bool persist = true)
        {
            string repo = Path.Combine(_root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, "docs"));
            DocA = Path.Combine(repo, "docs", "a.md");
            DocB = Path.Combine(repo, "docs", "b.md");
            Gone = Path.Combine(repo, "docs", "gone.md");
            Design = Path.Combine(repo, "docs", "design", "look.md");
            Plans = Path.Combine(repo, "docs", "plans", "v1.md");
            Directory.CreateDirectory(Path.GetDirectoryName(Design)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Plans)!);
            File.WriteAllText(DocA, "# A");
            File.WriteAllText(DocB, "# B");
            File.WriteAllText(Design, "# Look");
            File.WriteAllText(Plans, "# Plan");
            State = new DocumentWindowState(
                persist,
                path => path.StartsWith(repo, StringComparison.OrdinalIgnoreCase) ? repo : null,
                File.Exists,
                new GroupingOptions(null, StringComparison.OrdinalIgnoreCase)
            );
            ViewModel = new DocumentWindowViewModel(State, Surface, File.ReadAllText, new FakeTimeProvider(), Log);
            Window = new MainWindow(ViewModel, new Border(), Surface, Log);
            Window.Closed += (_, _) => IsClosed = true;
        }

        public bool IsClosed { get; private set; }

        public string DocA { get; }

        public string DocB { get; }

        public string Gone { get; }

        public string Design { get; }

        public string Plans { get; }

        public FakeDocumentSurface Surface { get; } = new();

        public RecordingAppLog Log { get; } = new();

        public DocumentWindowState State { get; }

        public DocumentWindowViewModel ViewModel { get; }

        public MainWindow Window { get; }

        public async Task OpenAsync(params string[] paths)
        {
            if (paths.Length > 0)
            {
                ViewModel.Open(paths);
            }

            Window.Show();
            Settle();
            await ViewModel.WhenRendersSettled();
            Settle();
        }

        public Panel Row(string path) =>
            Window
                .Tree.GetVisualDescendants()
                .OfType<Panel>()
                .Single(panel => panel.Classes.Contains("document") && panel.DataContext is DocumentItemViewModel row && row.Path == path);

        public Point Center(Visual visual) =>
            visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), Window)
            ?? throw new InvalidOperationException("The control is not in the window.");

        public void Click(Visual visual)
        {
            Point point = Center(visual);
            Window.MouseMove(point);
            Settle();
            Window.MouseDown(point, MouseButton.Left);
            Settle();
            Window.MouseUp(point, MouseButton.Left);
            Settle();
        }

        /// <summary>Asserts that keyboard focus is on the tree item whose row is the selected document's.</summary>
        public void AssertFocusOnSelectedRow()
        {
            TreeViewItem focused = Assert.IsType<TreeViewItem>(Window.FocusManager?.GetFocusedElement());
            Assert.Same(ViewModel.SelectedItem, focused.DataContext);
            Assert.IsType<DocumentItemViewModel>(focused.DataContext);
        }

        public void Press(Key key) => Press(key, RawInputModifiers.None);

        public void Press(Key key, RawInputModifiers modifiers)
        {
            Window.KeyPress(key, modifiers, PhysicalKey.None, null);
            Settle();
        }

        /// <summary>
        /// Runs queued UI work, a layout pass and a render tick, so bounds and the hit-tested scene reflect visibility changes
        /// before the next input.
        /// </summary>
        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            if (!IsClosed)
            {
                Window.Close();
            }

            ViewModel.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
