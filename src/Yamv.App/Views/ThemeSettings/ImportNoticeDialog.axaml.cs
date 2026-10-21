using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Yamv.Core.Theming;

namespace Yamv.App.Views.ThemeSettings;

/// <summary>
/// The Import notice drawn inside its window over a scrim: the file's card, what a theme file can change, and what its css can and
/// cannot do, with Cancel and Import. Import is the default unless the file does not load, when Cancel is. One notice is open at a
/// time.
/// </summary>
public partial class ImportNoticeDialog : UserControl
{
    private TaskCompletionSource<bool>? _pending;

    public ImportNoticeDialog() => InitializeComponent();

    /// <summary>Whether the notice is open, waiting for an answer.</summary>
    public bool IsOpen => _pending is not null;

    /// <summary>Opens the notice for <paramref name="inspection"/> and completes true when the user chose Import.</summary>
    /// <param name="inspection">The inspected file the card describes.</param>
    /// <exception cref="InvalidOperationException">The notice is already open.</exception>
    public Task<bool> ShowAsync(ThemeInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        if (_pending is not null)
        {
            throw new InvalidOperationException("An import notice is already open.");
        }

        bool imports = inspection.Errors.Count == 0;
        NoticeNameRun.Text = imports ? inspection.Name ?? inspection.FileName : inspection.FileName;
        NoticeDetailRun.Text = imports ? " " + Detail(inspection) : "";
        NoticePath.Text = inspection.FilePath;
        NoticeAddedAs.Text = $"Will be added as {inspection.AddedAsName}.";
        NoticeAddedAs.IsVisible = inspection.AddedAsName is not null;
        NoticeErrors.ItemsSource = inspection.Errors;
        NoticeErrors.IsVisible = inspection.Errors.Count > 0;
        NoticeWarnings.ItemsSource = inspection.Warnings;
        NoticeWarnings.IsVisible = inspection.Warnings.Count > 0;
        ShowChips(inspection, imports);

        Control choice = imports ? NoticeImport : NoticeCancel;
        NoticeImport.IsEnabled = imports;
        NoticeImport.IsDefault = imports;
        NoticeCancel.IsDefault = !imports;
        _pending = new TaskCompletionSource<bool>();
        IsVisible = true;

        // Focus after the layout pass that showing the notice needs, as the window's rename box does.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_pending is not null)
                {
                    choice.Focus();
                }
            },
            DispatcherPriority.Loaded
        );
        return _pending.Task;
    }

    /// <summary>Closes the open notice as Cancel; does nothing when none is open.</summary>
    public void Cancel() => Complete(false);

    /// <summary>The card's muted line: the author when the file names one, the halves it has and whether it carries css.</summary>
    private static string Detail(ThemeInspection inspection)
    {
        string halves = (inspection.HasLight, inspection.HasDark) switch
        {
            (true, true) => "light and dark",
            (true, false) => "light only",
            _ => "dark only",
        };

        string css = inspection.HasCss ? "has css" : "no css";
        return inspection.Author is { } author ? $"by {author} · {halves} · {css}" : $"{halves} · {css}";
    }

    /// <summary>
    /// Draws each half's chip the way the theme list draws its own; a half the theme lacks draws nothing, and a file that cannot be
    /// imported, even one whose theme loaded, draws no chip at all.
    /// </summary>
    private void ShowChips(ThemeInspection inspection, bool imports)
    {
        bool light = inspection.LightChip is not null;
        bool dark = inspection.DarkChip is not null;
        NoticeChip.IsVisible = imports && (light || dark);
        NoticeChipGrid.ColumnDefinitions = ColumnDefinitions.Parse(light && dark ? "*,*" : "*");
        DrawHalf(NoticeChipLight, NoticeChipLightBar, inspection.LightChip, column: 0);
        DrawHalf(NoticeChipDark, NoticeChipDarkBar, inspection.DarkChip, column: light ? 1 : 0);
    }

    private static void DrawHalf(Panel half, Border bar, ThemeChip? chip, int column)
    {
        half.IsVisible = chip is not null;
        Grid.SetColumn(half, column);
        if (chip is null)
        {
            return;
        }

        half.Background = BrushOf(chip.PageBackground);
        bar.Background = BrushOf(chip.Accent);
    }

    private static SolidColorBrush BrushOf(ColorValue colour) => new(Color.FromUInt32(colour.ToArgb()));

    private void Complete(bool result)
    {
        if (_pending is not { } pending)
        {
            return;
        }

        _pending = null;
        IsVisible = false;
        pending.SetResult(result);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Complete(false);

    private void OnPrimaryClick(object? sender, RoutedEventArgs e) => Complete(true);
}
