using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Yamv.Core.Diagnostics;
using Yamv.Core.Rendering;
using Yamv.Core.Session;

namespace Yamv.App.Controls;

/// <summary>
/// Shows rendered markdown in one native web view. The page template loads once; every document after that is
/// swapped in by script. The page never navigates: links, new windows and shortcuts come back as events. When the
/// native view is destroyed and created again (the control leaves the visual tree and comes back), the template loads
/// again and the latest document, theme, content width and confirm card flag are replayed into it. Every public member
/// must be called on the UI thread, and every event is raised there.
/// </summary>
public sealed class DocumentView : UserControl, IDocumentSurface
{
    private readonly IAppLog _log;
    private readonly NativeWebView _webView;
    private readonly Uri _templateUri;
    private readonly List<PendingCall> _pending = [];
    private readonly List<StateCall> _state = [];
    private bool _ready;
    private bool _draining;
    private bool _templateNavigationAllowed = true;
    private Uri? _documentUri;

    /// <summary>The full path of the shown document, as <see cref="ShowAsync"/> stored it; null before the first show.</summary>
    private string? _documentPath;
    private ShowState? _lastShow;
    private ScrollPosition? _lastScroll;

    /// <summary>The CSS value of the latest content width, from a show or a live change; null before either.</summary>
    private string? _latestContentWidth;
    private int _generation;
    private TaskCompletionSource? _rendered;

    public DocumentView(IAppLog log)
        : this(log, WebAssets.TemplateUri(log)) { }

    /// <summary>A view whose page loads from <paramref name="templateUri"/>; tests pass one that needs no extracted assets.</summary>
    internal DocumentView(IAppLog log, Uri templateUri)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(templateUri);
        _log = log;
        _templateUri = templateUri;
        _webView = new NativeWebView();
        _webView.EnvironmentRequested += OnEnvironmentRequested;
        _webView.AdapterCreated += (_, _) => WebView2Accelerators.Disable(_webView.TryGetPlatformHandle(), _log);
        _webView.AdapterDestroyed += OnAdapterDestroyed;
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NewWindowRequested += OnNewWindowRequested;
        _webView.WebMessageReceived += OnWebMessageReceived;
        _webView.Source = _templateUri;
        Content = _webView;
    }

    public event EventHandler<ScrollPosition>? ScrollChanged;

    public event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    public event EventHandler<KeyForwardedEventArgs>? KeyForwarded;

    public event EventHandler<bool>? DragOverChanged;

    /// <summary>
    /// Shows <paramref name="show"/>'s document at its content width, restoring its fragment if given, else its anchor,
    /// else its scroll fraction; a page that already shows the document keeps its own top block, or its pin to the top or
    /// bottom. The task completes once the page has rendered it, math, diagrams and scroll position included, or once a
    /// newer call replaces it. The width also becomes the latest content width, which a page that loads again gets back.
    /// </summary>
    public Task ShowAsync(DocumentShow show)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(show.Document);
        string width = CssWidth(show.ContentWidth, nameof(show));
        string fullPath = Path.GetFullPath(show.DocumentPath);
        _documentPath = fullPath;
        _documentUri = new Uri(fullPath);
        _lastScroll = null;
        int generation = ++_generation;
        var options = new RenderOptions(
            generation,
            fullPath,
            show.Scroll,
            show.Anchor?.Line,
            show.Anchor?.Offset ?? 0,
            show.Fragment,
            show.Document.HasMermaid,
            show.Document.HasMath,
            FolderUri(Path.GetDirectoryName(fullPath) ?? fullPath),
            FolderUri(show.GroupRoot),
            width
        );
        TakeOverContentWidth(width);
        _lastShow = new ShowState(show.Document.Html, options);
        string script = RenderScript(_lastShow);
        _rendered?.TrySetResult();
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rendered = rendered;
        return ShowCoreAsync(script, rendered);
    }

    /// <summary>Replaces the page's theme block with <paramref name="css"/>.</summary>
    public Task SetThemeCssAsync(string css)
    {
        Dispatcher.UIThread.VerifyAccess();
        return RunAsync(CallKind.Theme, "setTheme", $"window.yamv.setTheme({Json(css)})");
    }

    /// <summary>Changes the shown document's text measure in px, live; null makes it full width.</summary>
    /// <returns>
    /// False when the host could not run the page script; true when it ran, or when a newer call or a show replaced it.
    /// </returns>
    public async Task<bool> SetContentWidthAsync(double? px)
    {
        Dispatcher.UIThread.VerifyAccess();
        string width = CssWidth(px, nameof(px));
        _latestContentWidth = width;
        return !(await RunAsync(CallKind.ContentWidth, "setContentWidth", ContentWidthScript(width))).Failed;
    }

    /// <summary>
    /// Makes <paramref name="width"/>, which a show carries, the latest content width: it is remembered for a reload, so the
    /// replayed render and the replayed width agree, and a width call still queued, older than the show, is dropped.
    /// </summary>
    private void TakeOverContentWidth(string width)
    {
        _latestContentWidth = width;
        Remember(new StateCall(CallKind.ContentWidth, "setContentWidth", ContentWidthScript(width)));
        int queued = _pending.FindIndex(pending => pending.Kind == CallKind.ContentWidth);
        if (queued >= 0)
        {
            _pending[queued].Completion.TrySetResult(ScriptOutcome.NotRun);
            _pending.RemoveAt(queued);
        }
    }

    private static string ContentWidthScript(string width) => $"window.yamv.setContentWidth({Json(width)})";

    /// <summary>
    /// Tells the page whether a confirm card is up over it, so it holds Escape and Enter for the card. The page keeps the
    /// flag, and a page that loads again is told again, so its keys keep working after a reload.
    /// </summary>
    public Task SetCardOpenAsync(bool open)
    {
        Dispatcher.UIThread.VerifyAccess();
        return RunAsync(CallKind.CardOpen, "setCardOpen", CardOpenScript(open));
    }

    /// <summary>The script that tells the page whether a confirm card is up; a page that loads again replays it.</summary>
    internal static string CardOpenScript(bool open) => $"window.yamv.setCardOpen({(open ? "true" : "false")})";

    /// <summary>The CSS value of a content width: <c>"760px"</c>, or <c>"none"</c> for full width.</summary>
    private static string CssWidth(double? px, string parameterName)
    {
        if (px is not double value)
        {
            return "none";
        }

        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, px, "A content width is a positive number of px, or null for full width.");
        }

        return value.ToString(CultureInfo.InvariantCulture) + "px";
    }

    /// <summary>Focuses the page, opens the in-page find bar and focuses its field.</summary>
    public Task OpenFindAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        _webView.Focus();
        return RunAsync(CallKind.Other, "openFind", "window.yamv.openFind()");
    }

    /// <summary>Highlights every case-insensitive match of <paramref name="query"/> and moves to one.</summary>
    public Task<FindResult> FindAsync(string query, FindDirection direction)
    {
        Dispatcher.UIThread.VerifyAccess();
        return FindCoreAsync($"window.yamv.find({Json(query)}, {Json(DirectionName(direction))})");
    }

    /// <summary>
    /// Scrolls the shown document to the element whose id is <paramref name="fragment"/>, already decoded, without a render; a
    /// missing id does nothing.
    /// </summary>
    /// <returns>
    /// False when the host could not run the page script; true when it ran, or when a newer call or a show replaced it.
    /// </returns>
    public async Task<bool> ScrollToFragmentAsync(string fragment)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(fragment);
        return !(await RunAsync(CallKind.Other, "scrollToId", $"window.yamv.scrollToId({Json(fragment)})")).Failed;
    }

    /// <summary>Removes every find highlight, leaving the document text as it was.</summary>
    public Task ClearFindAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        return RunAsync(CallKind.Other, "clearFind", "window.yamv.clearFind()");
    }

    private async Task ShowCoreAsync(string script, TaskCompletionSource rendered)
    {
        ScriptOutcome outcome = await RunAsync(CallKind.Show, "render", script);
        if (!outcome.Ran)
        {
            rendered.TrySetResult();
        }

        await rendered.Task;
    }

    private async Task<FindResult> FindCoreAsync(string script)
    {
        string? json = (await RunAsync(CallKind.Other, "find", script)).Value;
        if (string.IsNullOrEmpty(json) || json == "null")
        {
            return new FindResult(0, 0);
        }

        try
        {
            return JsonSerializer.Deserialize(json, PageJsonContext.Default.FindResult) ?? new FindResult(0, 0);
        }
        catch (JsonException exception)
        {
            _log.LogWarning($"The page returned an unreadable find result: {LogText.Clean(json)}", exception);
            return new FindResult(0, 0);
        }
    }

    private static string RenderScript(ShowState show) =>
        $"void window.yamv.render({Json(show.Html)}, {JsonSerializer.Serialize(show.Options, PageJsonContext.Default.RenderOptions)})";

    private static string Json(string value) => JsonSerializer.Serialize(value, PageJsonContext.Default.String);

    private static string FolderUri(string folder)
    {
        string full = Path.GetFullPath(folder);
        return new Uri(WithTrailingSeparator(full)).AbsoluteUri;
    }

    private static string WithTrailingSeparator(string path) => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static string DirectionName(FindDirection direction) =>
        direction switch
        {
            FindDirection.First => "first",
            FindDirection.Next => "next",
            FindDirection.Previous => "prev",
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown find direction."),
        };

    /// <summary>
    /// Runs <paramref name="script"/> once the page is ready. Until then calls queue in order; a newer show, theme,
    /// content width or confirm card flag takes the place of the queued one of its kind, whose task completes without
    /// running. The latest call of each of those kinds is also kept, to replay into a page that loads again.
    /// </summary>
    private Task<ScriptOutcome> RunAsync(CallKind kind, string name, string script)
    {
        if (kind != CallKind.Other)
        {
            Remember(new StateCall(kind, name, script));
        }

        if (_ready && !_draining)
        {
            return InvokeAsync(name, script);
        }

        var call = new PendingCall(kind, name, script, NewCompletion());
        int superseded = kind == CallKind.Other ? -1 : _pending.FindIndex(pending => pending.Kind == kind);
        if (superseded >= 0)
        {
            _pending[superseded].Completion.TrySetResult(ScriptOutcome.NotRun);
            _pending[superseded] = call;
        }
        else
        {
            _pending.Add(call);
        }

        return call.Completion.Task;
    }

    private void Remember(StateCall call)
    {
        int index = _state.FindIndex(state => state.Kind == call.Kind);
        if (index >= 0)
        {
            _state[index] = call;
        }
        else
        {
            _state.Add(call);
        }
    }

    private static TaskCompletionSource<ScriptOutcome> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<ScriptOutcome> InvokeAsync(string name, string script)
    {
        try
        {
            return new ScriptOutcome(true, await _webView.InvokeScript(script), Failed: false);
        }
        // Every failure is logged and turned into "failed": the callers' tasks and the queue must never fault.
        catch (Exception exception)
        {
            _log.LogError($"Page script {name} failed: {Truncate(script)}", exception);
            return ScriptOutcome.ScriptFailed;
        }
    }

    private void OnPageReady()
    {
        _ready = true;
        IEnumerable<PendingCall> replay = _state
            .Where(state => !_pending.Exists(pending => pending.Kind == state.Kind))
            .Select(state => new PendingCall(state.Kind, state.Name, ReplayScript(state), NewCompletion()));
        _pending.InsertRange(0, [.. replay]);
        _ = DrainAsync();
    }

    /// <summary>
    /// A replayed show opens the document at the latest content width, from a show or a live change, whichever came last,
    /// and where the page last reported it scrolled to, when it did.
    /// </summary>
    private string ReplayScript(StateCall state)
    {
        if (state.Kind != CallKind.Show || _lastShow is null)
        {
            return state.Script;
        }

        RenderOptions options = _lastShow.Options with { ContentWidth = _latestContentWidth ?? _lastShow.Options.ContentWidth };
        if (_lastScroll is { } scroll)
        {
            _log.LogInfo($"Replaying the document at its last reported scroll position {Describe(scroll)}");
            options = options with
            {
                Scroll = scroll.Fraction,
                AnchorLine = scroll.Anchor?.Line,
                AnchorOffset = scroll.Anchor?.Offset ?? 0,
                Fragment = null,
            };
        }

        return RenderScript(_lastShow with { Options = options });
    }

    private async Task DrainAsync()
    {
        if (_draining)
        {
            return;
        }

        _draining = true;
        try
        {
            while (_ready && _pending.Count > 0)
            {
                PendingCall call = _pending[0];
                _pending.RemoveAt(0);
                ScriptOutcome outcome = ScriptOutcome.NotRun;
                try
                {
                    outcome = await InvokeAsync(call.Name, call.Script);
                }
                finally
                {
                    call.Completion.TrySetResult(outcome);
                }
            }
        }
        finally
        {
            _draining = false;
        }
    }

    private static string Truncate(string script) => script.Length <= 120 ? script : script[..120] + "…";

    /// <summary>
    /// Keeps the browser's user data in <c>LocalApplicationData/YAMV/webview2</c>, beside the asset cache and shared by
    /// every YAMV process, rather than in a folder next to the executable. A non-empty <c>WEBVIEW2_USER_DATA_FOLDER</c>
    /// wins, so a hidden run can keep a profile of its own; it is passed on explicitly, since Avalonia's WebView2
    /// adapter does not apply that variable itself.
    /// </summary>
    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        switch (e)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs windows:
                windows.UserDataFolder = WindowsUserDataFolder();
                break;
        }
    }

    private static string WindowsUserDataFolder()
    {
        string? overridden = Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER");
        return string.IsNullOrEmpty(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YAMV", "webview2")
            : overridden;
    }

    private void OnAdapterDestroyed(object? sender, WebViewAdapterEventArgs e)
    {
        _ready = false;
        _templateNavigationAllowed = true;
        _rendered?.TrySetResult();
        _log.LogInfo("The document page's web view was destroyed; the page will load again and replay its state");
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request == _templateUri)
        {
            if (_templateNavigationAllowed)
            {
                _templateNavigationAllowed = false;
                return;
            }

            e.Cancel = true;
            _log.LogInfo("Cancelled a reload of the document page");
            return;
        }

        e.Cancel = true;
        if (e.Request is not null)
        {
            RaiseLink(e.Request.OriginalString, e.Request, 0, LinkSource.Navigation);
        }
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Request is not null)
        {
            RaiseLink(e.Request.OriginalString, e.Request, 0, LinkSource.NewWindow);
        }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        PageMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(e.Body ?? string.Empty, PageJsonContext.Default.PageMessage);
        }
        catch (JsonException exception)
        {
            _log.LogWarning($"Ignored a malformed page message: {LogText.Clean(e.Body)}", exception);
            return;
        }

        if (message?.Type is null)
        {
            _log.LogWarning($"Ignored a page message without a type: {LogText.Clean(e.Body)}");
            return;
        }

        Dispatcher.UIThread.Post(() => Handle(message));
    }

    /// <summary>A scroll position for the log: <c>0.250, anchor line 42 at 0.350</c>, or <c>0.250, no anchor</c>.</summary>
    internal static string Describe(ScrollPosition scroll)
    {
        string fraction = scroll.Fraction.ToString("F3", CultureInfo.InvariantCulture);
        return scroll.Anchor is { } anchor
            ? string.Create(CultureInfo.InvariantCulture, $"{fraction}, anchor line {anchor.Line} at {anchor.Offset:F3}")
            : $"{fraction}, no anchor";
    }

    internal void Handle(PageMessage message)
    {
        switch (message.Type)
        {
            case "ready":
                OnPageReady();
                break;
            case "rendered" when message.Generation == _generation:
                _rendered?.TrySetResult();
                break;
            case "rendered":
                break;
            case "scroll" when ScrollPositionOf(message) is { } scroll && message.Generation >= _generation:
                _lastScroll = scroll;
                ScrollChanged?.Invoke(this, scroll);
                break;
            case "scroll":
                break;
            case "link" when message.Href is not null && message.Generation == _generation:
                RaiseLink(message.Href, Resolve(message.Href), message.Button ?? 0, LinkSource.Click);
                break;
            case "link" when message.Href is not null:
                _log.LogInfo(
                    $"A link from an older render dropped: '{LogText.Clean(message.Href)}' (render {message.Generation}, now {_generation})"
                );
                break;
            case "key" when message.Key is not null:
                KeyForwarded?.Invoke(this, new KeyForwardedEventArgs(message.Key, message.Ctrl, message.Meta, message.Shift, message.Alt));
                break;
            case "drag" when message.Over is bool over:
                DragOverChanged?.Invoke(this, over);
                break;
            case "find":
                break;
            case "blocked":
                _log.LogWarning(
                    $"Blocked image '{LogText.Clean(message.Src)}' in {LogText.Clean(_documentUri?.ToString())}: {LogText.Clean(message.Reason)}"
                );
                break;
            case "error":
                _log.LogWarning($"Page error in {LogText.Clean(message.Where)}: {LogText.Clean(message.Message)}");
                break;
            default:
                _log.LogWarning($"Ignored a page message of unknown type '{LogText.Clean(message.Type)}'");
                break;
        }
    }

    /// <summary>The position a <c>scroll</c> message reports: its fraction and anchor; null when it carries no fraction.</summary>
    internal static ScrollPosition? ScrollPositionOf(PageMessage message) =>
        message.Value is double fraction ? new ScrollPosition(fraction, AnchorOf(message.Line, message.Offset)) : null;

    /// <summary>
    /// The anchor a <c>scroll</c> message names: null unless its line is a whole number from 0 to <see cref="int.MaxValue"/>.
    /// An offset outside 0 to 1 is clamped into it, and a missing one is 0.
    /// </summary>
    private static ScrollAnchor? AnchorOf(double? line, double? offset)
    {
        if (line is not double value || value < 0 || value > int.MaxValue || Math.Floor(value) != value)
        {
            return null;
        }

        double within = offset is double given && double.IsFinite(given) ? Math.Clamp(given, 0, 1) : 0;
        return new ScrollAnchor((int)value, within);
    }

    /// <summary>Makes <paramref name="href"/> absolute against the document; a leading <c>//</c> means <c>https:</c>.</summary>
    private Uri? Resolve(string href)
    {
        string text = href.StartsWith("//", StringComparison.Ordinal) ? "https:" + href : href;
        if (_documentUri is not null && Uri.TryCreate(_documentUri, text, out Uri? relative))
        {
            return relative;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out Uri? absolute) ? absolute : null;
    }

    /// <summary>
    /// Raises <see cref="LinkActivated"/> for every link as it came, with the shown document's path; <c>LinkPolicy</c> decides
    /// what opens, a <c>file:</c> link with a host included.
    /// </summary>
    private void RaiseLink(string href, Uri? resolved, int button, LinkSource source)
    {
        string? documentPath = _documentPath;
        Dispatcher.UIThread.Post(() => LinkActivated?.Invoke(this, new LinkActivatedEventArgs(href, resolved, button, source, documentPath)));
    }

    private enum CallKind
    {
        Show,
        Theme,
        ContentWidth,
        CardOpen,
        Other,
    }

    /// <summary>What a script call came to: whether it ran, the JSON value it returned, and whether it failed.</summary>
    private readonly record struct ScriptOutcome(bool Ran, string? Value, bool Failed)
    {
        /// <summary>A call a newer one or a show replaced, or one dropped before it ran; nothing is wrong.</summary>
        public static ScriptOutcome NotRun { get; } = new(false, null, false);

        /// <summary>A call the page could not run.</summary>
        public static ScriptOutcome ScriptFailed { get; } = new(false, null, true);
    }

    private sealed record PendingCall(CallKind Kind, string Name, string Script, TaskCompletionSource<ScriptOutcome> Completion);

    private sealed record StateCall(CallKind Kind, string Name, string Script);

    private sealed record ShowState(string Html, RenderOptions Options);
}
