using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Yamv.App.Controls;
using Yamv.Core;
using Yamv.Core.Diagnostics;
using Yamv.Core.Rendering;

namespace Yamv.App.Views;

/// <summary>
/// The dev harness, started instead of the main window when the environment variable <c>YAMV_DEV_HARNESS</c> is <c>1</c>.
/// A test window for <see cref="DocumentView"/>: renders the markdown file given as the first argument
/// (default <c>docs/design/fixtures/sample.md</c>). <c>--theme dark</c> starts dark, <c>--find</c> runs a find once the
/// document shows, <c>--scroll 0.8</c> opens it scrolled to that fraction, <c>--full-width</c> drops the
/// 800 px content width, <c>--toggle-theme</c> switches the theme 2 s after the document shows, so a run on a hidden
/// desktop exercises <see cref="DocumentView.SetThemeCssAsync"/> without key presses, <c>--open-find</c> opens the
/// in-page find bar, and <c>--reattach</c> takes the view out of the window and puts it back, so its web view is
/// destroyed and created again. Keys: T (or Ctrl+T in the page) toggles the theme, F (or Ctrl+G in the page) runs the find.
/// </summary>
public partial class HarnessWindow : Window
{
    private const string LightTheme = """
        :root { --yamv-page-background: #ffffff; --yamv-text: #1f2328; --yamv-text-secondary: #31373d; --yamv-text-muted: #656d76;
          --yamv-heading: #111418; --yamv-accent: #0969da; --yamv-accent-soft: #b6d4f5; --yamv-line: #d8dee4; --yamv-quote: #d0d7de;
          --yamv-chrome-background: #f6f8fa; --yamv-selection-background: #e3edfa; --yamv-selection-text: #0b3d7a; --yamv-hover-background: #eaeef2;
          --yamv-code-background: #f6f8fa; --yamv-code-line: #e4e8ec; --yamv-syntax-keyword: #cf222e; --yamv-syntax-string: #0a3069;
          --yamv-syntax-comment: #6e7781; --yamv-syntax-type: #8250df; --yamv-syntax-number: #0550ae; }
        """;

    private const string DarkTheme = """
        :root { --yamv-page-background: #0f1318; --yamv-text: #e6edf3; --yamv-text-secondary: #c9d1d9; --yamv-text-muted: #8b949e;
          --yamv-heading: #f0f6fc; --yamv-accent: #4493f8; --yamv-accent-soft: #1f3b5e; --yamv-line: #2a3139; --yamv-quote: #3d444d;
          --yamv-chrome-background: #0a0d11; --yamv-selection-background: #182a40; --yamv-selection-text: #e6edf3; --yamv-hover-background: #161b22;
          --yamv-code-background: #161b22; --yamv-code-line: #262c34; --yamv-syntax-keyword: #ff7b72; --yamv-syntax-string: #a5d6ff;
          --yamv-syntax-comment: #8b949e; --yamv-syntax-type: #d2a8ff; --yamv-syntax-number: #79c0ff;
          --yamv-find-match: #6b5a1a; --yamv-find-current: #b07a12; --yamv-warning: #d29922; --yamv-warning-background: #2b2111;
          --yamv-warning-line: #5a4417; }
        """;

    private readonly FileAppLog _log = new(AppPaths.LogFile);
    private readonly DocumentView _view;
    private bool _dark;

    public HarnessWindow()
    {
        InitializeComponent();
        string[] args = Environment.GetCommandLineArgs()[1..];
        _dark = HasOption(args, "--theme", "dark");
        _view = new DocumentView(_log);
        _view.LinkActivated += (_, e) => _log.LogInfo($"Harness: LinkActivated href='{e.Href}' resolved='{e.ResolvedUri}' button={e.Button}");
        _view.KeyForwarded += OnKeyForwarded;
        _view.ScrollChanged += (_, value) => _log.LogInfo($"Harness: ScrollChanged {value.ToString("F3", CultureInfo.InvariantCulture)}");
        Content = _view;
        KeyDown += OnKeyDown;
        _ = StartAsync(DocumentPath(args), args);
    }

    private static double ScrollOption(string[] args)
    {
        int index = Array.IndexOf(args, "--scroll");
        return index >= 0 && index + 1 < args.Length && double.TryParse(args[index + 1], CultureInfo.InvariantCulture, out double value) ? value : 0;
    }

    private static bool HasOption(string[] args, string name, string value)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && args[index + 1] == value;
    }

    private static string DocumentPath(string[] args)
    {
        string? given = args.FirstOrDefault(arg =>
            !arg.StartsWith("--", StringComparison.Ordinal)
            && arg != "dark"
            && arg != "light"
            && !double.TryParse(arg, CultureInfo.InvariantCulture, out _)
        );
        if (given is not null)
        {
            return Path.GetFullPath(given);
        }

        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "docs", "design", "fixtures", "sample.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("No file argument and docs/design/fixtures/sample.md was not found above the app folder.");
    }

    private static string GroupRoot(string documentPath)
    {
        string folder = Path.GetDirectoryName(documentPath)!;
        for (DirectoryInfo? current = new(folder); current is not null; current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
        }

        return folder;
    }

    private async Task StartAsync(string documentPath, string[] args)
    {
        try
        {
            RenderedDocument document = new MarkdownRenderer().Render(await File.ReadAllTextAsync(documentPath));
            Title = $"YAMV harness: {document.Title ?? Path.GetFileName(documentPath)}";
            await _view.SetThemeCssAsync(_dark ? DarkTheme : LightTheme);
            await _view.SetContentWidthAsync(args.Contains("--full-width") ? null : 800);
            await _view.ShowAsync(document, documentPath, GroupRoot(documentPath), ScrollOption(args), null);
            _log.LogInfo($"Harness: showing {documentPath} ({(_dark ? "dark" : "light")})");
            if (args.Contains("--find"))
            {
                await RunFindAsync();
            }

            if (args.Contains("--toggle-theme"))
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                await ToggleThemeAsync();
            }

            if (args.Contains("--open-find"))
            {
                await _view.OpenFindAsync();
                _log.LogInfo($"Harness: OpenFindAsync done, view focused={_view.IsKeyboardFocusWithin}");
            }

            if (args.Contains("--reattach"))
            {
                await ReattachAsync();
            }
        }
        // A harness start-up failure is logged rather than lost in a discarded task.
        catch (Exception exception)
        {
            _log.LogError($"Harness: could not show {documentPath}", exception);
        }
    }

    private async Task ReattachAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        Content = null;
        _log.LogInfo("Harness: took the document view out of the window");
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Content = _view;
        _log.LogInfo("Harness: put the document view back");
    }

    private async Task RunFindAsync()
    {
        FindResult first = await _view.FindAsync("the", FindDirection.First);
        _log.LogInfo($"Harness: FindAsync(\"the\", First) -> {first.Current} of {first.Total}");
        FindResult next = await _view.FindAsync("the", FindDirection.Next);
        _log.LogInfo($"Harness: FindAsync(\"the\", Next) -> {next.Current} of {next.Total}");
    }

    private async Task ToggleThemeAsync()
    {
        _dark = !_dark;
        await _view.SetThemeCssAsync(_dark ? DarkTheme : LightTheme);
        _log.LogInfo($"Harness: theme {(_dark ? "dark" : "light")}");
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.T)
        {
            _ = ToggleThemeAsync();
        }
        else if (e.Key == Key.F)
        {
            _ = RunFindAsync();
        }
    }

    private void OnKeyForwarded(object? sender, KeyForwardedEventArgs e)
    {
        _log.LogInfo($"Harness: KeyForwarded key='{e.Key}' ctrl={e.Ctrl} meta={e.Meta} shift={e.Shift} alt={e.Alt}");
        if ((e.Ctrl || e.Meta) && e.Key.Equals("t", StringComparison.OrdinalIgnoreCase))
        {
            _ = ToggleThemeAsync();
        }
        else if ((e.Ctrl || e.Meta) && e.Key.Equals("g", StringComparison.OrdinalIgnoreCase))
        {
            _ = RunFindAsync();
        }
    }
}
