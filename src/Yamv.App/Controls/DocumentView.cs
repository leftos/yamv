using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Yamv.Core.Diagnostics;
using Yamv.Core.Rendering;

namespace Yamv.App.Controls;

/// <summary>
/// Shows rendered markdown in one native web view. The page template loads once; every document after that is
/// swapped in by script. The page never navigates: links, new windows and shortcuts come back as events. When the
/// native view is destroyed and created again (the control leaves the visual tree and comes back), the template loads
/// again and the latest document, theme and content width are replayed into it. Every public member must be called on
/// the UI thread, and every event is raised there.
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
    private ShowState? _lastShow;
    private double? _lastScroll;
    private int _generation;
    private TaskCompletionSource? _rendered;

    public DocumentView(IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _templateUri = WebAssets.TemplateUri(log);
        _webView = new NativeWebView();
        _webView.AdapterCreated += (_, _) => WebView2Accelerators.Disable(_webView.TryGetPlatformHandle(), _log);
        _webView.AdapterDestroyed += OnAdapterDestroyed;
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NewWindowRequested += OnNewWindowRequested;
        _webView.WebMessageReceived += OnWebMessageReceived;
        _webView.Source = _templateUri;
        Content = _webView;
    }

    public event EventHandler<double>? ScrollChanged;

    public event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    public event EventHandler<KeyForwardedEventArgs>? KeyForwarded;

    /// <summary>
    /// Shows <paramref name="document"/>, restoring <paramref name="fragment"/> if given, else <paramref name="scroll"/>.
    /// The task completes once the page has rendered it, math, diagrams and scroll position included, or once a newer
    /// call replaces it.
    /// </summary>
    /// <param name="document">The rendered markdown.</param>
    /// <param name="documentPath">The markdown file's path; relative links and images resolve against it.</param>
    /// <param name="groupRoot">The folder <c>file:</c> images must stay inside.</param>
    /// <param name="scroll">The scroll position as a fraction, 0 to 1, of the scrollable height.</param>
    /// <param name="fragment">An element id to scroll to, which wins over <paramref name="scroll"/>.</param>
    public Task ShowAsync(RenderedDocument document, string documentPath, string groupRoot, double scroll, string? fragment)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(document);
        string fullPath = Path.GetFullPath(documentPath);
        _documentUri = new Uri(fullPath);
        _lastScroll = null;
        int generation = ++_generation;
        var options = new RenderOptions(
            generation,
            scroll,
            fragment,
            document.HasMermaid,
            document.HasMath,
            FolderUri(Path.GetDirectoryName(fullPath) ?? fullPath),
            FolderUri(groupRoot)
        );
        _lastShow = new ShowState(document.Html, options);
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

    /// <summary>Sets the content column's maximum width in px; null makes it full width.</summary>
    public Task SetContentWidthAsync(double? px)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (px is double value && (!double.IsFinite(value) || value <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(px), px, "A content width is a positive number of px, or null for full width.");
        }

        string width = px is double pixels ? pixels.ToString(CultureInfo.InvariantCulture) + "px" : "none";
        return RunAsync(CallKind.ContentWidth, "setContentWidth", $"window.yamv.setContentWidth({Json(width)})");
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
            _log.LogWarning($"The page returned an unreadable find result: {json}", exception);
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
    /// Runs <paramref name="script"/> once the page is ready. Until then calls queue in order; a newer show, theme or
    /// content width takes the place of the queued one of its kind, whose task completes without running. The latest
    /// call of each of those kinds is also kept, to replay into a page that loads again.
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
            return new ScriptOutcome(true, await _webView.InvokeScript(script));
        }
        // Every failure is logged and turned into "did not run": the callers' tasks and the queue must never fault.
        catch (Exception exception)
        {
            _log.LogError($"Page script {name} failed: {Truncate(script)}", exception);
            return ScriptOutcome.NotRun;
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

    /// <summary>A replayed show opens the document where the page last reported it scrolled to, when it did.</summary>
    private string ReplayScript(StateCall state)
    {
        if (state.Kind != CallKind.Show || _lastShow is null || _lastScroll is not double scroll)
        {
            return state.Script;
        }

        _log.LogInfo($"Replaying the document at its last reported scroll position {scroll.ToString("F3", CultureInfo.InvariantCulture)}");
        return RenderScript(_lastShow with { Options = _lastShow.Options with { Scroll = scroll, Fragment = null } });
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
            RaiseLink(e.Request.OriginalString, e.Request, 0);
        }
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Request is not null)
        {
            RaiseLink(e.Request.OriginalString, e.Request, 0);
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
            _log.LogWarning($"Ignored a malformed page message: {e.Body}", exception);
            return;
        }

        if (message?.Type is null)
        {
            _log.LogWarning($"Ignored a page message without a type: {e.Body}");
            return;
        }

        Dispatcher.UIThread.Post(() => Handle(message));
    }

    private void Handle(PageMessage message)
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
            case "scroll" when message.Value is double value && message.Generation >= _generation:
                _lastScroll = value;
                ScrollChanged?.Invoke(this, value);
                break;
            case "scroll":
                break;
            case "link" when message.Href is not null:
                RaiseLink(message.Href, Resolve(message.Href), message.Button ?? 0);
                break;
            case "key" when message.Key is not null:
                KeyForwarded?.Invoke(this, new KeyForwardedEventArgs(message.Key, message.Ctrl, message.Meta, message.Shift, message.Alt));
                break;
            case "find":
                break;
            case "blocked":
                _log.LogWarning($"Blocked image '{message.Src}' in {_documentUri}: {message.Reason}");
                break;
            case "error":
                _log.LogWarning($"Page error in {message.Where}: {message.Message}");
                break;
            default:
                _log.LogWarning($"Ignored a page message of unknown type '{message.Type}'");
                break;
        }
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

    private void RaiseLink(string href, Uri? resolved, int button)
    {
        // A file: URL with a host names a network share; opening it would reach out over SMB.
        if (resolved is not null && resolved.IsFile && !string.IsNullOrEmpty(resolved.Host))
        {
            _log.LogWarning($"Ignored a link to '{href}' in {_documentUri}: a file: link may not name a host");
            return;
        }

        Dispatcher.UIThread.Post(() => LinkActivated?.Invoke(this, new LinkActivatedEventArgs(href, resolved, button)));
    }

    private enum CallKind
    {
        Show,
        Theme,
        ContentWidth,
        Other,
    }

    /// <summary>What a script call came to: whether it ran, and the JSON value it returned.</summary>
    private readonly record struct ScriptOutcome(bool Ran, string? Value)
    {
        public static ScriptOutcome NotRun { get; } = new(false, null);
    }

    private sealed record PendingCall(CallKind Kind, string Name, string Script, TaskCompletionSource<ScriptOutcome> Completion);

    private sealed record StateCall(CallKind Kind, string Name, string Script);

    private sealed record ShowState(string Html, RenderOptions Options);
}
