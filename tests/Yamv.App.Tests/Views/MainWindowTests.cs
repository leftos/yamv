using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using Yamv.App.Controls;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.App.ViewModels;
using Yamv.App.Views;
using Yamv.Core.Documents;
using Yamv.Core.Grouping;
using Yamv.Core.Links;
using Yamv.Core.Platform;
using Yamv.Core.Session;
using Yamv.Core.Widths;

namespace Yamv.App.Tests.Views;

public sealed class MainWindowTests
{
    [Fact]
    public Task The_title_bar_Open_button_opens_the_picker() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            fixture.Files.DocumentsToPick = [fixture.DocB];
            TitleBar bar = fixture.Window.WindowTitleBar;

            Assert.Same(bar.OpenButton, bar.TitleBarTools.Children[0]);
            Assert.Equal(MainWindow.OpenToolTip, ToolTip.GetTip(bar.OpenButton));
            fixture.ClickEvent(bar.OpenButton);

            Assert.Equal(1, fixture.Files.DocumentPicks);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task The_Open_button_opens_the_picker_in_a_viewer_window() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture(persist: false);
            await fixture.OpenAsync(fixture.DocA);

            fixture.ClickEvent(fixture.Window.WindowTitleBar.OpenButton);

            Assert.Equal(1, fixture.Files.DocumentPicks);
        });

    [Fact]
    public Task Dropping_documents_opens_them_and_selects_the_first() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);

            await fixture.Window.OpenDroppedAsync([fixture.DocB, fixture.Other]);
            fixture.Settle();

            Assert.Equal([fixture.DocA, fixture.DocB, fixture.Other], fixture.State.Documents);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
            Assert.Empty(fixture.Toasts.Items);
        });

    [Fact]
    public Task A_mixed_drop_opens_the_documents_and_refuses_the_rest_once() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);

            await fixture.Window.OpenDroppedAsync(["pic.png", fixture.DocB, "tool.ps1"]);
            fixture.Settle();

            Assert.Equal([fixture.DocA, fixture.DocB], fixture.State.Documents);
            ToastItem toast = Assert.Single(fixture.Toasts.Items);
            Assert.Equal((ToastKind.Refusal, DocumentFileTypes.DropHint, 1), (toast.Kind, toast.Text, toast.Count));
        });

    [Fact]
    public Task A_drop_of_only_non_documents_toasts_the_hint() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);

            await fixture.Window.OpenDroppedAsync(["run.exe", string.Empty]);
            fixture.Settle();

            Assert.Equal([fixture.DocA], fixture.State.Documents);
            Assert.Equal(DocumentFileTypes.DropHint, Assert.Single(fixture.Toasts.Items).Text);
        });

    [Fact]
    public Task A_drop_of_an_open_document_selects_it() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            await fixture.Window.OpenDroppedAsync([fixture.DocB]);
            fixture.Settle();

            Assert.Equal([fixture.DocA, fixture.DocB], fixture.State.Documents);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Hovering_with_no_document_shows_the_grey_form() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);

            DragDropEffects effects = fixture.Window.OnDragPaths(["run.exe"]);
            fixture.Settle();

            Assert.Equal(DragDropEffects.None, effects);
            Assert.True(fixture.Window.DropPopup.IsOpen);
            Assert.Contains("no", fixture.Window.DropOverlay.Form.Classes);
            Assert.Equal(DocumentFileTypes.DropHint, fixture.Window.DropOverlay.Say.Text);
        });

    [Fact]
    public Task Hovering_with_a_document_shows_drop_to_open() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);

            DragDropEffects effects = fixture.Window.OnDragPaths(["run.exe", fixture.DocB]);
            fixture.Settle();

            Assert.Equal(DragDropEffects.Copy, effects);
            Assert.True(fixture.Window.DropPopup.IsOpen);
            Assert.DoesNotContain("no", fixture.Window.DropOverlay.Form.Classes);
            Assert.Equal(DropOverlay.OpenText, fixture.Window.DropOverlay.Say.Text);
        });

    [Fact]
    public Task The_empty_state_takes_a_drag_over_its_blank_area() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            // The window's fill is the theme resource YamvPageBackground, which the test app never sets.
            fixture.Window.Background = Brushes.White;
            await fixture.OpenAsync();
            Panel column = fixture.Window.DocumentColumn;
            Point blank = column.TranslatePoint(new Point(4, column.Bounds.Height - 4), fixture.Window)!.Value;

            IInputElement? hit = fixture.Window.InputHitTest(blank);

            Assert.True(DragDrop.GetAllowDrop(Assert.IsAssignableFrom<Interactive>(hit)));
        });

    [Fact]
    public Task A_drop_reports_copy_to_its_source() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);

            Assert.Equal(DragDropEffects.Copy, fixture.Window.OnDropPaths([fixture.DocB]));
        });

    [Fact]
    public Task A_drop_with_no_paths_reports_none() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);

            Assert.Equal(DragDropEffects.None, fixture.Window.OnDropPaths([]));
            Assert.Equal([fixture.DocA], fixture.State.Documents);
        });

    [Fact]
    public Task Minimising_during_a_drag_hides_the_overlay_for_good() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            fixture.Window.OnDragPaths([fixture.DocB]);
            fixture.Settle();
            Assert.True(fixture.Window.DropPopup.IsOpen);

            fixture.Window.WindowState = WindowState.Minimized;
            fixture.Settle();
            fixture.Window.WindowState = WindowState.Normal;
            fixture.Settle();

            Assert.False(fixture.Window.DropPopup.IsOpen);
        });

    [Fact]
    public Task A_drag_with_no_local_paths_shows_the_grey_form() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            fixture.Surface.RaiseDragOver(true);

            DragDropEffects effects = fixture.Window.OnDragPaths([]);
            fixture.Settle();

            Assert.Equal(DragDropEffects.None, effects);
            Assert.True(fixture.Window.DropPopup.IsOpen);
            Assert.Contains("no", fixture.Window.DropOverlay.Form.Classes);
        });

    [Fact]
    public Task A_drag_with_no_local_paths_keeps_its_grey_form_across_the_title_bar() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            fixture.Surface.RaiseDragOver(true);
            fixture.Window.OnDragPaths([]);
            fixture.Surface.RaiseDragOver(false);

            fixture.Window.OnDragPaths([]);
            fixture.Time.Advance(DropOverlayViewModel.Grace);
            fixture.Settle();

            Assert.True(fixture.Window.DropPopup.IsOpen);
            Assert.Contains("no", fixture.Window.DropOverlay.Form.Classes);
        });

    [Fact]
    public Task A_cancelled_drag_hides_the_overlay_after_the_grace() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            fixture.Surface.RaiseDragOver(true);
            fixture.Settle();
            Assert.True(fixture.Window.DropPopup.IsOpen);

            fixture.Time.Advance(DropOverlayViewModel.Grace);
            fixture.Settle();

            Assert.False(fixture.Window.DropPopup.IsOpen);
        });

    [Fact]
    public Task A_drop_sorted_after_the_window_closed_opens_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);

            Task dropped = fixture.Window.OpenDroppedAsync([fixture.DocB, "run.exe"]);
            fixture.Window.Close();
            await dropped;

            Assert.Equal([fixture.DocA], fixture.State.Documents);
            Assert.Empty(fixture.Toasts.Items);
        });

    [Fact]
    public Task Closing_the_window_unhooks_the_drag_handlers() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            fixture.Window.OnDragPaths([fixture.DocB]);
            fixture.Settle();
            Assert.True(fixture.Surface.HasDragSubscribers);

            fixture.Window.Close();
            fixture.Settle();

            Assert.False(fixture.Surface.HasDragSubscribers);
            Assert.False(fixture.Window.DropPopup.IsOpen);
        });

    [Fact]
    public Task Tree_shows_repo_group_badge_folder_and_documents() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.Gone);

            Panel row = fixture.Row(fixture.Gone);

            Assert.Contains("MISSING", VisibleTexts(row));
            Assert.DoesNotContain("MISSING", VisibleTexts(fixture.Row(fixture.DocA)));
        });

    [Fact]
    public Task Close_button_closes_its_document_without_selecting_it_first() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.Gone, fixture.DocA);

            Assert.Equal(fixture.Gone, fixture.ViewModel.SelectedPath);
            Assert.True(fixture.Window.MissingPane.IsVisible);
            Assert.False(fixture.Window.DocumentHost.IsVisible);
            Assert.False(fixture.Window.EmptyState.IsVisible);
            Assert.Equal(fixture.Gone, fixture.Window.MissingPane.MissingPath.Text);
            Assert.Contains("It was moved or deleted:", VisibleTexts(fixture.Window.MissingPane));
        });

    [Fact]
    public Task Selected_unreadable_document_shows_unreadable_pane_and_marks_its_row() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Reader = path =>
                path == fixture.DocA ? throw new IOException("The file is locked.", unchecked((int)0x80070020)) : File.ReadAllText(path);

            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            fixture.Settle();

            Assert.Equal(fixture.DocA, fixture.ViewModel.SelectedPath);
            Assert.True(fixture.Window.UnreadablePane.IsVisible);
            Assert.False(fixture.Window.DocumentHost.IsVisible);
            Assert.False(fixture.Window.MissingPane.IsVisible);
            Assert.Equal(fixture.DocA, fixture.Window.UnreadablePane.UnreadablePath.Text);
            List<string> texts = VisibleTexts(fixture.Window.UnreadablePane);
            Assert.Contains("This file can't be read", texts);
            Assert.Contains("It is in use by another program.", texts);
            Assert.Contains("YAMV tries again when the file changes.", texts);

            Panel row = fixture.Row(fixture.DocA);
            Assert.DoesNotContain("MISSING", VisibleTexts(row));
            Assert.DoesNotContain("missing", row.Classes);
            Assert.True(UnreadableGlyph(row).IsEffectivelyVisible);
            Assert.False(UnreadableGlyph(fixture.Row(fixture.DocB)).IsEffectivelyVisible);
        });

    [Fact]
    public Task Retry_on_the_unreadable_pane_shows_the_document_again() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            bool locked = true;
            fixture.Reader = path => locked ? throw new IOException("The file is locked.", unchecked((int)0x80070020)) : File.ReadAllText(path);

            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            fixture.Settle();
            Assert.True(fixture.Window.UnreadablePane.IsVisible);

            locked = false;
            // A pointer over the pane's buttons hit-tests to the window (their fill is YamvPageBackground, which the test app
            // never sets), and raising Click does not run a Button's command, so the test runs the command Retry is bound to.
            ICommand retry = Assert.IsAssignableFrom<ICommand>(fixture.Window.UnreadablePane.RetryButton.Command);
            retry.Execute(null);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Settle();

            Assert.False(fixture.Window.UnreadablePane.IsVisible);
            Assert.True(fixture.Window.DocumentHost.IsVisible);
            Assert.Equal(fixture.DocA, fixture.Surface.Shown[^1].DocumentPath);
            Assert.False(UnreadableGlyph(fixture.Row(fixture.DocA)).IsEffectivelyVisible);
        });

    [Fact]
    public Task Remove_from_list_on_the_unreadable_pane_closes_the_selected_document() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Reader = path =>
                path == fixture.DocA ? throw new IOException("The file is locked.", unchecked((int)0x80070020)) : File.ReadAllText(path);

            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            fixture.Settle();
            Assert.True(fixture.Window.UnreadablePane.IsVisible);

            Button remove = fixture.Window.UnreadablePane.RemoveButton;
            ICommand command = Assert.IsAssignableFrom<ICommand>(remove.Command);
            command.Execute(remove.CommandParameter);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Settle();

            Assert.DoesNotContain(fixture.DocA, fixture.State.Documents);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
            Assert.False(fixture.Window.UnreadablePane.IsVisible);
            Assert.True(fixture.Window.DocumentHost.IsVisible);
        });

    [Fact]
    public Task Viewer_with_one_document_hides_splitter_and_sidebar() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture(persist: false);
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
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
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            bool mac = OperatingSystem.IsMacOS();

            fixture.Surface.RaiseKey(new KeyForwardedEventArgs("w", ctrl: !mac, meta: mac, shift: false, alt: false));
            await fixture.ViewModel.WhenRendersSettled();

            Assert.DoesNotContain(fixture.DocA, fixture.State.Documents);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Forwarded_command_close_bracket_from_the_page_steps_the_width_wider() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            string docs = Path.GetDirectoryName(fixture.DocA)!;
            Assert.True(fixture.Widths.Set(docs, ContentWidth.FromPx(760)));
            fixture.Window.Width = 1600;
            await fixture.OpenAsync(fixture.DocA);
            bool mac = OperatingSystem.IsMacOS();

            fixture.Surface.RaiseKey(new KeyForwardedEventArgs("]", ctrl: !mac, meta: mac, shift: false, alt: false));
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(800, fixture.Widths.Get(docs)?.Px);
            Assert.Equal(new double?[] { 800 }, fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task Command_and_the_physical_left_bracket_key_steps_the_width_whatever_key_the_layout_reports() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            string docs = Path.GetDirectoryName(fixture.DocA)!;
            Assert.True(fixture.Widths.Set(docs, ContentWidth.FromPx(763)));
            fixture.Window.Width = 1600;
            await fixture.OpenAsync(fixture.DocA);

            fixture.Window.KeyPress(Key.Oem8, CommandModifier, PhysicalKey.BracketLeft, "ü");
            fixture.Settle();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(760, fixture.Widths.Get(docs)?.Px);
            Assert.Equal(new double?[] { 760 }, fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task Command_and_the_physical_right_bracket_key_steps_the_width_wider() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            string docs = Path.GetDirectoryName(fixture.DocA)!;
            Assert.True(fixture.Widths.Set(docs, ContentWidth.FromPx(760)));
            fixture.Window.Width = 1600;
            await fixture.OpenAsync(fixture.DocA);

            fixture.Window.KeyPress(Key.OemPlus, CommandModifier, PhysicalKey.BracketRight, "+");
            fixture.Settle();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(800, fixture.Widths.Get(docs)?.Px);
            Assert.Equal(new double?[] { 800 }, fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task Command_and_a_left_bracket_key_code_from_another_physical_key_does_not_step() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            fixture.Window.Width = 1600;
            await fixture.OpenAsync(fixture.DocA);

            fixture.Window.KeyPress(Key.OemOpenBrackets, CommandModifier, PhysicalKey.Digit8, "[");
            fixture.Settle();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.False(File.Exists(fixture.WidthsFile));
            Assert.Empty(fixture.Surface.ContentWidths);
        });

    [Fact]
    public Task Command_W_on_a_viewer_with_one_document_closes_the_window() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture(persist: false);
            await fixture.OpenAsync(fixture.DocA);

            fixture.Press(Key.W, CommandModifier);

            Assert.True(fixture.IsClosed);
        });

    [Fact]
    public Task Sidebar_width_above_the_limit_reads_480() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            fixture.Window.SidebarWidth = 600;

            Assert.Equal(480, fixture.Window.SidebarWidth);
        });

    [Fact]
    public Task Context_menus_list_the_actions_of_their_row_kind() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            string reveal = OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Reveal in Explorer";

            Assert.Equal([reveal, "Copy path", "Close", "Close group"], Headers(fixture.Row(fixture.DocA).ContextMenu));
            Assert.Equal([reveal, "Copy path"], Headers(fixture.FolderRow("docs").ContextMenu));
            Assert.Equal([reveal, "Copy path", "Close group"], Headers(fixture.GroupHeader("repo").ContextMenu));
        });

    [Fact]
    public Task Right_click_on_an_unselected_document_selects_and_shows_it_then_opens_its_menu() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            ContextMenu menu = fixture.RightClick(fixture.Row(fixture.DocB));
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
            Assert.Equal(fixture.DocB, fixture.Surface.Shown[^1].DocumentPath);
            Assert.True(menu.IsOpen);
            menu.Close();
        });

    [Fact]
    public Task Right_click_on_a_folder_row_or_group_header_keeps_the_selection() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            ContextMenu folderMenu = fixture.RightClick(fixture.FolderRow("docs"));
            Assert.True(folderMenu.IsOpen);
            folderMenu.Close();
            ContextMenu groupMenu = fixture.RightClick(fixture.GroupHeader("repo"));
            Assert.True(groupMenu.IsOpen);
            groupMenu.Close();
            fixture.Settle();

            Assert.Equal(fixture.DocA, fixture.ViewModel.SelectedPath);
            Assert.Equal(fixture.DocA, Assert.IsType<DocumentItemViewModel>(fixture.ViewModel.SelectedItem).Path);
        });

    [Fact]
    public Task Menu_close_selects_the_next_document_and_focuses_its_row_then_the_empty_state() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB, fixture.Gone);

            fixture.Choose(fixture.RightClick(fixture.Row(fixture.DocA)), "Close");
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Settle();

            Assert.DoesNotContain(fixture.DocA, fixture.State.Documents);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
            fixture.AssertFocusOnSelectedRow();

            fixture.Choose(fixture.RightClick(fixture.Row(fixture.DocB)), "Close");
            fixture.Choose(fixture.RightClick(fixture.Row(fixture.Gone)), "Close");
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.IsEmpty);
            Assert.True(fixture.Window.EmptyState.IsVisible);
        });

    [Fact]
    public Task Menu_close_on_the_last_document_in_tree_order_selects_the_previous() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            fixture.Choose(fixture.RightClick(fixture.Row(fixture.DocB)), "Close");
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal([fixture.DocA], fixture.State.Documents);
            Assert.Equal(fixture.DocA, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Menu_close_group_on_a_document_row_or_group_header_removes_that_group() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.Other, fixture.DocA, fixture.DocB);

            fixture.Choose(fixture.RightClick(fixture.Row(fixture.DocB)), "Close group");
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Settle();

            Assert.Equal([fixture.Other], fixture.State.Documents);
            Assert.DoesNotContain(AllRows(fixture.ViewModel.Items), row => row is GroupItemViewModel { Name: "repo" });
            fixture.AssertFocusOnSelectedRow();

            fixture.Choose(fixture.RightClick(fixture.GroupHeader(Path.GetDirectoryName(fixture.Other)!)), "Close group");
            await fixture.ViewModel.WhenRendersSettled();

            Assert.True(fixture.ViewModel.IsEmpty);
        });

    [Fact]
    public Task Copy_path_and_reveal_send_each_row_kind_its_path() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB, fixture.Gone);
            string docs = Path.Combine(fixture.Repo, "docs");

            foreach (
                (Control row, string path) in new (Control, string)[]
                {
                    (fixture.Row(fixture.DocB), fixture.DocB),
                    (fixture.FolderRow("docs"), docs),
                    (fixture.GroupHeader("repo"), fixture.Repo),
                }
            )
            {
                fixture.Choose(fixture.RightClick(row), "Copy path");
                Assert.Equal(path, fixture.Files.Copied[^1]);
            }

            fixture.Choose(fixture.RightClick(fixture.Row(fixture.DocB)), MainWindow.RevealMenuHeader);
            fixture.Choose(fixture.RightClick(fixture.FolderRow("docs")), MainWindow.RevealMenuHeader);
            fixture.Choose(fixture.RightClick(fixture.GroupHeader("repo")), MainWindow.RevealMenuHeader);
            fixture.Choose(fixture.RightClick(fixture.Row(fixture.Gone)), MainWindow.RevealMenuHeader);

            Assert.Equal(
                [(fixture.DocB, RevealKind.File), (docs, RevealKind.Folder), (fixture.Repo, RevealKind.Folder), (fixture.Gone, RevealKind.File)],
                fixture.Files.Reveals
            );
        });

    [Fact]
    public Task A_failed_copy_path_toasts() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            fixture.Files.CopyFailure = new IOException("The clipboard refused.");

            fixture.Choose(fixture.RightClick(fixture.Row(fixture.DocA)), "Copy path");

            ToastItem toast = Assert.Single(fixture.Toasts.Items);
            Assert.Equal((ToastKind.Warning, "Couldn't copy the path; see the log"), (toast.Kind, toast.Text));
            Assert.Contains(fixture.Log.Entries, entry => entry.Message == "Copy path: failed.");
        });

    [Fact]
    public Task Command_O_opens_the_picked_files_and_selects_the_first() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            fixture.Click(fixture.Row(fixture.DocA));
            fixture.Files.DocumentsToPick = [fixture.Plans, fixture.Design];

            fixture.Press(Key.O, CommandModifier);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(1, fixture.Files.DocumentPicks);
            Assert.Equal([fixture.DocA, fixture.DocB, fixture.Plans, fixture.Design], fixture.State.Documents);
            Assert.Equal(fixture.Plans, fixture.ViewModel.SelectedPath);
            Assert.Equal(fixture.Plans, fixture.Surface.Shown[^1].DocumentPath);
        });

    [Fact]
    public Task Forwarded_command_o_from_the_page_opens_the_picked_files() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            fixture.Files.DocumentsToPick = [fixture.DocB];
            bool mac = OperatingSystem.IsMacOS();

            fixture.Surface.RaiseKey(new KeyForwardedEventArgs("o", ctrl: !mac, meta: mac, shift: false, alt: false));
            fixture.Settle();
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal([fixture.DocA, fixture.DocB], fixture.State.Documents);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Command_O_in_a_viewer_adds_the_picked_file_and_shows_the_tree() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture(persist: false);
            await fixture.OpenAsync(fixture.DocA);
            Assert.False(fixture.Window.Sidebar.IsVisible);
            fixture.Files.DocumentsToPick = [fixture.DocB];

            fixture.Press(Key.O, CommandModifier);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Settle();

            Assert.Equal([fixture.DocA, fixture.DocB], fixture.State.Documents);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
            Assert.True(fixture.Window.Sidebar.IsVisible);
        });

    [Fact]
    public Task Command_O_with_a_cancelled_pick_changes_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);

            fixture.Press(Key.O, CommandModifier);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal(1, fixture.Files.DocumentPicks);
            Assert.Equal([fixture.DocA, fixture.DocB], fixture.State.Documents);
            Assert.Equal(fixture.DocA, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task A_failed_file_pick_toasts() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            fixture.Files.PickFailure = new IOException("The picker went away.");

            fixture.Press(Key.O, CommandModifier);

            ToastItem toast = Assert.Single(fixture.Toasts.Items);
            Assert.Equal((ToastKind.Warning, "Couldn't open the files; see the log"), (toast.Kind, toast.Text));
            Assert.Contains(fixture.Log.Entries, entry => entry.Message == "Open files: failed.");
        });

    [Fact]
    public Task Command_O_picking_an_open_file_selects_it_without_adding_it_twice() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            fixture.Files.DocumentsToPick = [fixture.DocB];

            fixture.Press(Key.O, CommandModifier);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal([fixture.DocA, fixture.DocB], fixture.State.Documents);
            Assert.Equal(fixture.DocB, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Empty_state_open_files_button_opens_the_picked_files() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync();
            fixture.Files.DocumentsToPick = [fixture.DocA];
            Button button = fixture.Window.EmptyState.OpenFilesButton;
            Assert.True(button.IsEffectivelyEnabled);

            fixture.Click(button);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal([fixture.DocA], fixture.State.Documents);
            Assert.Equal(fixture.DocA, fixture.ViewModel.SelectedPath);
        });

    [Fact]
    public Task Locate_replaces_the_missing_document_in_place_and_keeps_its_scroll_position() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            fixture.Surface.RaiseScroll(0.4, null);
            File.Delete(fixture.DocA);
            fixture.ViewModel.Reload();
            fixture.Settle();
            Assert.True(fixture.Window.MissingPane.IsVisible);
            string moved = Path.Combine(fixture.Repo, "docs", "moved.md");
            File.WriteAllText(moved, "# Moved");
            fixture.Files.ReplacementToPick = moved;
            Button locate = fixture.Window.MissingPane.LocateButton;
            Assert.True(locate.IsEffectivelyEnabled);

            fixture.ClickEvent(locate);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Settle();

            Assert.Equal([fixture.DocA], fixture.Files.ReplacementRequests);
            Assert.Equal([moved, fixture.DocB], fixture.State.Documents);
            Assert.Equal(moved, fixture.ViewModel.SelectedPath);
            Assert.Equal((moved, 0.4), (fixture.Surface.Shown[^1].DocumentPath, fixture.Surface.Shown[^1].Scroll));
            Assert.False(fixture.Window.MissingPane.IsVisible);
        });

    [Fact]
    public Task Locate_with_a_cancelled_pick_changes_nothing() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.Gone, fixture.DocA);

            fixture.ClickEvent(fixture.Window.MissingPane.LocateButton);
            await fixture.ViewModel.WhenRendersSettled();

            Assert.Equal([fixture.Gone], fixture.Files.ReplacementRequests);
            Assert.Equal([fixture.Gone, fixture.DocA], fixture.State.Documents);
            Assert.Equal(fixture.Gone, fixture.ViewModel.SelectedPath);
            Assert.True(fixture.Window.MissingPane.IsVisible);
        });

    [Fact]
    public Task A_failed_locate_toasts() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.Gone, fixture.DocA);
            fixture.Files.ReplacementFailure = new IOException("The picker went away.");

            fixture.ClickEvent(fixture.Window.MissingPane.LocateButton);

            ToastItem toast = Assert.Single(fixture.Toasts.Items);
            Assert.Equal((ToastKind.Warning, "Couldn't locate the file; see the log"), (toast.Kind, toast.Text));
            Assert.Contains(fixture.Log.Entries, entry => entry.Message == "Locate: failed.");
        });

    [Fact]
    public Task Disposing_the_fixture_waits_for_a_document_read_still_in_flight() =>
        Headless.RunAsync(async () =>
        {
            using var opened = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            fixture.Reader = path =>
            {
                using var reader = new StreamReader(path);
                opened.Set();
                release.Wait(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
                return reader.ReadToEnd();
            };
            fixture.ViewModel.SelectNext();
            Assert.True(opened.Wait(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken), "the read of b.md never started");

            ValueTask disposing = fixture.DisposeAsync();
            release.Set();
            await disposing;

            Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Repo)));
        });

    [Fact]
    public Task The_more_button_is_the_second_title_bar_tool() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            TitleBar bar = fixture.Window.WindowTitleBar;

            int widthIndex = bar.TitleBarTools.Children.IndexOf(bar.ContentWidthButton);

            Assert.True(widthIndex >= 0);
            Assert.Same(bar.MoreButton, bar.TitleBarTools.Children[widthIndex + 1]);
            Assert.Equal("More", ToolTip.GetTip(bar.MoreButton));
            Assert.True(bar.MoreButton.IsEffectivelyEnabled);
        });

    [Fact]
    public Task A_click_on_the_more_button_opens_the_menu_with_one_item_Theme_settings() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            Button more = fixture.Window.WindowTitleBar.MoreButton;

            fixture.ClickEvent(more);

            Assert.True(fixture.Window.MorePopup.IsOpen);
            Assert.Contains("open", more.Classes);
            Button item = Assert.Single(fixture.Window.MoreMenu.GetVisualDescendants().OfType<Button>());
            Assert.Same(fixture.Window.ThemeSettingsItem, item);
            // The key hint is docked right, so it comes first in the visual tree.
            Assert.Equal([OperatingSystem.IsMacOS() ? "⌘," : "Ctrl+,", "Theme settings…"], VisibleTexts(fixture.Window.MoreMenu));
        });

    [Fact]
    public Task A_second_click_closes_the_menu() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            Button more = fixture.Window.WindowTitleBar.MoreButton;
            fixture.ClickEvent(more);
            Assert.True(fixture.Window.MorePopup.IsOpen);

            fixture.ClickEvent(more);

            Assert.False(fixture.Window.MorePopup.IsOpen);
            Assert.DoesNotContain("open", more.Classes);
        });

    [Fact]
    public Task The_item_raises_ThemeSettingsRequested_once_and_closes_the_menu() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            List<object?> senders = [];
            fixture.Window.ThemeSettingsRequested += (sender, _) => senders.Add(sender);
            fixture.ClickEvent(fixture.Window.WindowTitleBar.MoreButton);

            fixture.ClickEvent(fixture.Window.ThemeSettingsItem);

            Assert.Equal([fixture.Window], senders);
            Assert.False(fixture.Window.MorePopup.IsOpen);
            Assert.DoesNotContain("open", fixture.Window.WindowTitleBar.MoreButton.Classes);
        });

    [Fact]
    public Task Enter_on_the_focused_more_button_opens_the_menu_and_Esc_closes_it_back_to_the_button() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            Button more = fixture.Window.WindowTitleBar.MoreButton;
            Assert.True(more.Focusable && more.IsTabStop);
            more.Focus(NavigationMethod.Tab);
            fixture.Settle();

            fixture.Press(Key.Enter);

            Assert.True(fixture.Window.MorePopup.IsOpen);
            TopLevel menuTop = TopLevel.GetTopLevel(fixture.Window.MoreMenu) ?? throw new InvalidOperationException("The menu is not shown.");
            Assert.Same(fixture.Window.ThemeSettingsItem, menuTop.FocusManager?.GetFocusedElement());

            menuTop.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            fixture.Settle();
            Assert.Same(fixture.Window.ThemeSettingsItem, menuTop.FocusManager?.GetFocusedElement());

            menuTop.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            fixture.Settle();

            Assert.False(fixture.Window.MorePopup.IsOpen);
            Assert.Same(more, fixture.Window.FocusManager?.GetFocusedElement());
        });

    [Fact]
    public Task Ctrl_comma_raises_ThemeSettingsRequested() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            int requests = 0;
            fixture.Window.ThemeSettingsRequested += (_, _) => requests++;

            fixture.Window.KeyPress(Key.OemComma, CommandModifier, PhysicalKey.Comma, ",");
            fixture.Settle();
            Assert.Equal(1, requests);

            fixture.Click(fixture.Row(fixture.DocA));
            await fixture.ViewModel.WhenRendersSettled();
            fixture.AssertFocusOnSelectedRow();
            fixture.Window.KeyPress(Key.OemComma, CommandModifier, PhysicalKey.Comma, ",");
            fixture.Settle();

            Assert.Equal(2, requests);
        });

    [Fact]
    public Task Forwarded_command_comma_from_the_page_raises_ThemeSettingsRequested() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            int requests = 0;
            fixture.Window.ThemeSettingsRequested += (_, _) => requests++;
            bool mac = OperatingSystem.IsMacOS();

            fixture.Surface.RaiseKey(new KeyForwardedEventArgs(",", ctrl: !mac, meta: mac, shift: false, alt: false));
            fixture.Settle();

            Assert.Equal(1, requests);
        });

    [Fact]
    public Task Ctrl_comma_with_the_tree_focused_leaves_focus_where_the_request_put_it() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA, fixture.DocB);
            Button more = fixture.Window.WindowTitleBar.MoreButton;
            fixture.Window.ThemeSettingsRequested += (_, _) => more.Focus(NavigationMethod.Tab);
            fixture.Click(fixture.Row(fixture.DocA));
            await fixture.ViewModel.WhenRendersSettled();
            fixture.AssertFocusOnSelectedRow();

            fixture.Window.KeyPress(Key.OemComma, CommandModifier, PhysicalKey.Comma, ",");
            fixture.Settle();

            Assert.Same(more, fixture.Window.FocusManager?.GetFocusedElement());
        });

    [Fact]
    public Task Opening_the_more_menu_closes_the_width_popover() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            fixture.ClickEvent(fixture.Window.WindowTitleBar.ContentWidthButton);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Settle();
            Assert.True(fixture.Window.WidthPopup.IsOpen);

            fixture.ClickEvent(fixture.Window.WindowTitleBar.MoreButton);

            Assert.True(fixture.Window.MorePopup.IsOpen);
            Assert.False(fixture.Window.WidthPopup.IsOpen);
            Assert.DoesNotContain("open", fixture.Window.WindowTitleBar.ContentWidthButton.Classes);
        });

    [Fact]
    public Task Opening_the_width_popover_closes_the_more_menu() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            fixture.ClickEvent(fixture.Window.WindowTitleBar.MoreButton);
            Assert.True(fixture.Window.MorePopup.IsOpen);

            fixture.ClickEvent(fixture.Window.WindowTitleBar.ContentWidthButton);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Settle();

            Assert.True(fixture.Window.WidthPopup.IsOpen);
            Assert.False(fixture.Window.MorePopup.IsOpen);
            Assert.DoesNotContain("open", fixture.Window.WindowTitleBar.MoreButton.Classes);
        });

    [Fact]
    public Task A_clicked_link_in_the_window_reaches_the_opener() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DocumentWindowViewModel.SelectedPath) && fixture.ViewModel.SelectedPath is not null)
                {
                    opened.TrySetResult();
                }
            };

            fixture.Surface.RaiseLink(new LinkActivatedEventArgs("b.md", new Uri(fixture.DocB), 0, LinkSource.Click, fixture.DocA));
            await opened.Task.WaitAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            await fixture.ViewModel.WhenRendersSettled();
            fixture.Settle();

            string expected =
                RealPath.Resolve(fixture.DocB)?.Path ?? throw new InvalidOperationException($"The test path '{fixture.DocB}' did not resolve.");
            Assert.Equal(expected, fixture.ViewModel.SelectedPath, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(expected, fixture.Surface.Shown[^1].DocumentPath, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level == "error");
        });

    [Fact]
    public Task Closing_the_window_unhooks_the_opener() =>
        Headless.RunAsync(async () =>
        {
            await using var fixture = new Fixture();
            await fixture.OpenAsync(fixture.DocA);
            Assert.True(fixture.Surface.HasLinkSubscribers);

            fixture.Window.Close();
            fixture.Settle();

            Assert.False(fixture.Surface.HasLinkSubscribers);
        });

    private static List<string?> Headers(ContextMenu? menu) =>
        [.. Assert.IsType<ContextMenu>(menu).Items.OfType<MenuItem>().Select(item => item.Header as string)];

    /// <summary>The platform's command key: Cmd on macOS, Ctrl elsewhere.</summary>
    private static RawInputModifiers CommandModifier => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static List<string> VisibleTexts(Visual root) =>
        [
            .. root.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && text.Text is not null)
                .Select(text => text.Text!),
        ];

    private static Viewbox UnreadableGlyph(Panel row) =>
        row.GetVisualDescendants().OfType<Viewbox>().Single(view => view.Classes.Contains("unreadableGlyph"));

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
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "yamv-window-" + Guid.NewGuid().ToString("N"));

        public Fixture(bool persist = true)
        {
            string repo = Path.Combine(_root, "repo");
            Repo = repo;
            Directory.CreateDirectory(Path.Combine(repo, "docs"));
            Other = Path.Combine(_root, "other", "x.md");
            Directory.CreateDirectory(Path.GetDirectoryName(Other)!);
            File.WriteAllText(Other, "# X");
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
            WidthsFile = Path.Combine(_root, "data", "widths.json");
            Widths = new WidthStore(WidthsFile, Log);
            Time = new FakeTimeProvider();
            Toasts = new ToastCenter(Time);
            ViewModel = new DocumentWindowViewModel(State, Surface, new DocumentWindowServices(Widths, path => Reader(path), Time, Log, Toasts));
            Window = new MainWindow(ViewModel, new Border(), Surface, Log) { FileActions = Files };
            Window.Closed += (_, _) => IsClosed = true;
        }

        public bool IsClosed { get; private set; }

        /// <summary>The window's clock, which its toasts fade on.</summary>
        public FakeTimeProvider Time { get; }

        /// <summary>The window's toasts, as the view model shows them.</summary>
        public ToastCenter Toasts { get; }

        /// <summary>The window's widths file, in the fixture's temp folder.</summary>
        public string WidthsFile { get; }

        /// <summary>The view model's width store.</summary>
        public WidthStore Widths { get; }

        public string Repo { get; }

        /// <summary>A document outside the repo, grouped by its folder <c>other</c>.</summary>
        public string Other { get; }

        public FakeFileActions Files { get; } = new();

        public string DocA { get; }

        public string DocB { get; }

        public string Gone { get; }

        public string Design { get; }

        public string Plans { get; }

        /// <summary>Reads a document; a test replaces it to make one file fail to read without a real lock.</summary>
        public Func<string, string> Reader { get; set; } = File.ReadAllText;

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

        public Panel FolderRow(string name) =>
            Window
                .Tree.GetVisualDescendants()
                .OfType<Panel>()
                .Single(panel => panel.Classes.Contains("folder") && panel.DataContext is FolderItemViewModel row && row.Name == name);

        public Border GroupHeader(string name) =>
            Window
                .Tree.GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Classes.Contains("group") && border.DataContext is GroupItemViewModel row && row.Name == name);

        /// <summary>Presses and releases the right button over <paramref name="row"/> and returns the row's context menu.</summary>
        public ContextMenu RightClick(Control row)
        {
            Point point = Center(row);
            Window.MouseMove(point);
            Settle();
            Window.MouseDown(point, MouseButton.Right);
            Settle();
            Window.MouseUp(point, MouseButton.Right);
            Settle();
            return Assert.IsType<ContextMenu>(row.ContextMenu);
        }

        /// <summary>Clicks the item of <paramref name="menu"/> with this header, then closes the menu as a click does.</summary>
        public void Choose(ContextMenu menu, string header)
        {
            MenuItem item = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header));
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            Settle();
        }

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

        /// <summary>
        /// Raises <paramref name="button"/>'s click as a click would. A pointer over the missing pane's buttons hit-tests to the
        /// window instead: their fill is the theme resource <c>YamvPageBackground</c>, which the test app never sets.
        /// </summary>
        public void ClickEvent(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

        public async ValueTask DisposeAsync()
        {
            if (!IsClosed)
            {
                Window.Close();
            }

            ViewModel.Dispose();
            await ViewModel.WhenRendersSettled();
            Directory.Delete(_root, recursive: true);
        }
    }
}
