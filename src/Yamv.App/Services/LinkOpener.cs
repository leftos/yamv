using Avalonia.Threading;
using Yamv.App.Controls;
using Yamv.App.ViewModels;
using Yamv.Core;
using Yamv.Core.Diagnostics;
using Yamv.Core.Links;

namespace Yamv.App.Services;

/// <summary>The documents of a window, as a <see cref="LinkOpener"/> sees them: the window's view model.</summary>
internal interface ILinkDocuments
{
    /// <summary>The root folder of the group holding <paramref name="documentPath"/>, or null when the document is not open.</summary>
    /// <param name="documentPath">The document that holds the link.</param>
    string? GroupRootOf(string documentPath);

    /// <summary>Whether <paramref name="documentPath"/> is the selected document.</summary>
    /// <param name="documentPath">The document that holds the link.</param>
    bool IsShown(string documentPath);

    /// <summary>Opens <paramref name="path"/> in the window and selects it, scrolled to <paramref name="fragment"/> when given.</summary>
    /// <param name="path">
    /// The document's path as the link policy decided it, or the shown document's own spelling when the link resolves to it;
    /// it need not exist.
    /// </param>
    /// <param name="fragment">The heading to scroll to, already decoded; null for none.</param>
    void OpenLinkedDocument(string path, string? fragment);
}

/// <summary>What a <see cref="LinkOpener"/> hands its work to: the browser, the file actions, the policy and the thread pool.</summary>
/// <param name="OpenInBrowser">
/// Opens an http, https or mailto URI in the platform's browser or mail app, off the UI thread, inside <see cref="Run"/>; false
/// when it did not.
/// </param>
/// <param name="FileActions">Reads the window's current file actions, which open a viewable file in its default app.</param>
/// <param name="Decide">Decides what a link opens; <see cref="LinkPolicy.Decide(Uri?, LinkContext)"/> outside tests.</param>
/// <param name="Run">Runs blocking work off the UI thread; <c>Task.Run</c> outside tests.</param>
/// <param name="Time">The clock a drop's batch waits on.</param>
/// <param name="Confirm">Shows the confirm card with a title and a body; true when the user chose Open.</param>
internal sealed record LinkOpenerSeams(
    Func<Uri, Task<bool>> OpenInBrowser,
    Func<IFileActions> FileActions,
    Func<Uri?, LinkContext, LinkDecision> Decide,
    Func<Func<LinkDecision>, Task<LinkDecision>> Run,
    TimeProvider Time,
    Func<string, string, Task<bool>> Confirm
);

/// <summary>How a file dropped on the page, which reaches a <see cref="LinkOpener"/> as a new-window link, leaves it.</summary>
/// <param name="Signalled">Whether the page signalled a file drag just now, which tells a drop from a document's new-window link.</param>
/// <param name="Open">Opens the dropped paths as one batch, in drop order; it is the window's, and sorts them itself.</param>
internal sealed record LinkDropRoute(Func<bool> Signalled, Action<IReadOnlyList<string>> Open);

/// <summary>
/// Turns a clicked link in a window's document into the one action its <see cref="LinkDecision"/> names, and tells the user
/// in a toast when a link opens nothing. The decision is made off the UI thread, and only the newest click acts. A file
/// dropped on the page is no click: it skips all of that and goes to the window with the rest of its drop.
/// </summary>
internal sealed class LinkOpener : IDisposable
{
    /// <summary>The toast of a link the app could not act on.</summary>
    internal const string LinkFailedToast = "Couldn't open the link; see the log";

    /// <summary>How long after a drop's last file its batch is handed over.</summary>
    internal static readonly TimeSpan DropBatchWait = TimeSpan.FromMilliseconds(50);

    private readonly ILinkDocuments _documents;
    private readonly IToasts _toasts;
    private readonly IAppLog _log;
    private readonly LinkOpenerSeams _seams;
    private readonly LinkDropRoute _drops;
    private readonly List<string> _droppedPaths = [];
    private ITimer? _dropTimer;
    private int _newestClick;
    private bool _disposed;

    /// <summary>Creates the link opener of one window.</summary>
    /// <param name="documents">The window's documents, which give a link its group root and open linked documents.</param>
    /// <param name="toasts">Where a link that opens nothing is shown.</param>
    /// <param name="log">Where every link and its outcome is logged.</param>
    /// <param name="seams">The browser, the file actions, the policy and the thread pool.</param>
    /// <param name="drops">How a file dropped on the page reaches the window.</param>
    public LinkOpener(ILinkDocuments documents, IToasts toasts, IAppLog log, LinkOpenerSeams seams, LinkDropRoute drops)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(toasts);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(drops);
        _documents = documents;
        _toasts = toasts;
        _log = log;
        _seams = seams;
        _drops = drops;
    }

    /// <summary>Handles a link the surface raised; every failure is logged, so it never throws.</summary>
    public async void OnLinkActivated(object? sender, LinkActivatedEventArgs e)
    {
        try
        {
            await HandleAsync(e);
        }
        catch (Exception exception)
        {
            _log.LogError($"Link: could not handle {LogText.Clean(e?.Href)}.", exception);
            _toasts.Show(ToastKind.Warning, LinkFailedToast);
        }
    }

    /// <summary>Decides <paramref name="link"/> off the UI thread and acts on the decision back on it.</summary>
    /// <remarks>
    /// Only a click acts, or a file dropped on the page. The group root is read on the UI thread from the document that holds
    /// the link, not the selection; a decision that finishes after a newer click, or after the window closed, does nothing.
    /// </remarks>
    internal async Task HandleAsync(LinkActivatedEventArgs link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (IsDrop(link))
        {
            QueueDrop(link.ResolvedUri!.LocalPath);
            return;
        }

        if (link.Source != LinkSource.Click)
        {
            _log.LogInfo($"Link from a {SourceName(link.Source)} ignored: {LogText.Clean(link.Href)}");
            return;
        }

        if (link.DocumentPath is not string documentPath || _documents.GroupRootOf(documentPath) is not string groupRoot)
        {
            _log.LogInfo($"Link ignored: the document that held it is no longer open: {LogText.Clean(link.Href)}");
            return;
        }

        int click = ++_newestClick;
        var context = new LinkContext(groupRoot, RealPath.Resolve);
        LinkDecision decision;
        try
        {
            decision = await _seams.Run(() => ToShownSpelling(_seams.Decide(link.ResolvedUri, context), documentPath, context));
        }
        catch (Exception exception)
        {
            _log.LogError($"Link: could not decide what {LogText.Clean(link.Href)} opens.", exception);
            if (IsNewest(click, link))
            {
                _toasts.Show(ToastKind.Refusal, NotPlainPathText(link.Href));
            }

            return;
        }

        if (IsNewest(click, link))
        {
            await ActAsync(decision, link, context, click);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _dropTimer?.Dispose();
        _dropTimer = null;
        OpenDropped();
    }

    /// <summary>
    /// A drop is a new-window request for an absolute <c>file:</c> URI. The page's drag signal starts a batch; handing a batch over
    /// ends the signal, so a file joining one already queued does not need it.
    /// </summary>
    private bool IsDrop(LinkActivatedEventArgs link) =>
        link is { Source: LinkSource.NewWindow, ResolvedUri: { IsAbsoluteUri: true, IsFile: true } }
        && (_droppedPaths.Count > 0 || _drops.Signalled());

    /// <summary>Adds a dropped path to the batch. A drop raises one link per file, so each arrival restarts the wait.</summary>
    private void QueueDrop(string path)
    {
        _droppedPaths.Add(path);
        _dropTimer?.Dispose();
        _dropTimer = _seams.Time.CreateTimer(_ => Dispatcher.UIThread.Post(OpenDropped), null, DropBatchWait, Timeout.InfiniteTimeSpan);
    }

    private void OpenDropped()
    {
        if (_droppedPaths.Count == 0)
        {
            return;
        }

        _dropTimer?.Dispose();
        _dropTimer = null;
        string[] paths = [.. _droppedPaths];
        _droppedPaths.Clear();
        if (_disposed)
        {
            _log.LogInfo($"Link: a drop arrived after the window closed; nothing opened: {LogText.Clean(string.Join(", ", paths))}");
            return;
        }

        _log.LogInfo($"Link: {paths.Length} file(s) dropped on the page.");
        _drops.Open(paths);
    }

    private static string SourceName(LinkSource source) =>
        source switch
        {
            LinkSource.Click => "click",
            LinkSource.Navigation => "navigation",
            LinkSource.NewWindow => "new window",
            _ => source.ToString(),
        };

    private static string NotPlainPathText(string href) => $"YAMV doesn't open {ToastValue.Code(href)}: not a plain local path";

    /// <summary>Whether the decision of <paramref name="click"/> may act: the window is open and no newer click came.</summary>
    private bool IsNewest(int click, LinkActivatedEventArgs link)
    {
        if (_disposed)
        {
            _log.LogInfo($"Link decided after the window closed; nothing opened: {LogText.Clean(link.Href)}");
            return false;
        }

        if (click != _newestClick)
        {
            _log.LogInfo($"Link superseded by a newer click; nothing opened: {LogText.Clean(link.Href)}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gives an <see cref="OpenDocument"/> decision the spelling of the shown document it names, so a link to that document
    /// through a junction or a symbolic link scrolls it instead of opening a second row; other decisions pass unchanged.
    /// </summary>
    /// <param name="decision">The decision the link policy made.</param>
    /// <param name="documentPath">The path the document holding the link was shown with.</param>
    /// <param name="context">The link's context, whose resolver gives that document's real path.</param>
    private static LinkDecision ToShownSpelling(LinkDecision decision, string documentPath, LinkContext context) =>
        decision is OpenDocument document
        && context.Resolve(documentPath) is ResolvedPath shown
        && AppPaths.PathComparer.Equals(shown.Path, document.Path)
            ? document with
            {
                Path = documentPath,
            }
            : decision;

    /// <summary>Does the one thing <paramref name="decision"/> names.</summary>
    private Task ActAsync(LinkDecision decision, LinkActivatedEventArgs link, LinkContext context, int click)
    {
        switch (decision)
        {
            case OpenExternal external:
                return OpenExternalAsync(external, link);
            case OpenDocument document:
                _log.LogInfo($"Link: opening the document {LogText.Clean(document.Path)} (linked as {LogText.Clean(document.LinkPath)}).");
                _documents.OpenLinkedDocument(document.Path, document.Fragment);
                return Task.CompletedTask;
            case OpenWithDefaultApp app:
                return OpenWithDefaultAppAsync(app);
            case Confirm confirm:
                return ConfirmAsync(confirm, link, context, click);
            case Ignore ignore:
                Refuse(ignore, link, context.GroupRoot);
                return Task.CompletedTask;
            default:
                _log.LogError($"Link: no action for a {decision.GetType().Name} decision on {LogText.Clean(link.Href)}; nothing opened.");
                _toasts.Show(ToastKind.Warning, LinkFailedToast);
                return Task.CompletedTask;
        }
    }

    private async Task OpenExternalAsync(OpenExternal external, LinkActivatedEventArgs link)
    {
        if (external.DroppedFields.Count > 0)
        {
            _log.LogInfo(
                $"Link: left out the mailto fields {LogText.Clean(string.Join(", ", external.DroppedFields))} of {LogText.Clean(link.Href)}"
            );
        }

        Uri uri = external.Uri;
        if (!uri.IsAbsoluteUri || uri.IsFile || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            _log.LogError($"Link: refused to hand {LogText.Clean(uri.OriginalString)} to the browser: not an http, https or mailto URI.");
            _toasts.Show(ToastKind.Warning, LinkFailedToast);
            return;
        }

        _log.LogInfo($"Link: opening {LogText.Clean(uri.AbsoluteUri)} in the browser.");
        bool opened = false;
        try
        {
            await _seams.Run(() =>
            {
                opened = _seams.OpenInBrowser(uri).GetAwaiter().GetResult();
                return external;
            });
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Link: the browser could not open {LogText.Clean(uri.AbsoluteUri)}.", exception);
            opened = false;
        }

        if (!opened)
        {
            _log.LogWarning($"Link: the browser did not open {LogText.Clean(uri.AbsoluteUri)}.");
            _toasts.Show(ToastKind.Warning, "Couldn't open the link in your browser");
        }
    }

    private Task OpenWithDefaultAppAsync(OpenWithDefaultApp app)
    {
        IFileActions fileActions = _seams.FileActions();
        return StartDefaultAppAsync(app, app.Path, () => fileActions.OpenViewable(app.Path));
    }

    /// <summary>
    /// Asks before opening <paramref name="confirm"/>'s path, and opens it only on an answer of Open to the newest click, with its
    /// document still shown. The name is sanitised here, since the whole sentence is longer than a sanitised value may be.
    /// </summary>
    private async Task ConfirmAsync(Confirm confirm, LinkActivatedEventArgs link, LinkContext context, int click)
    {
        if (!MayAsk(confirm, link, click))
        {
            return;
        }

        string name = ToastValue.Plain(FolderName(confirm.Path));
        var text = ConfirmText.For(confirm.Path, name, confirm.IsDirectory, OperatingSystem.IsMacOS());
        _log.LogInfo($"Link: asking before opening {LogText.Clean(confirm.Path)}.");
        if (!await _seams.Confirm(text.Title, text.Body))
        {
            _log.LogInfo($"Link: {LogText.Clean(confirm.Path)} not opened; the user cancelled.");
            return;
        }

        if (MayAsk(confirm, link, click))
        {
            await OpenIfUnchangedAsync(confirm, link, context, click);
        }
    }

    /// <summary>
    /// Decides the link again and opens <paramref name="confirm"/>'s path only when the answer is the same, since the card can
    /// stand for as long as the user leaves it.
    /// </summary>
    private async Task OpenIfUnchangedAsync(Confirm confirm, LinkActivatedEventArgs link, LinkContext context, int click)
    {
        LinkDecision current = await _seams.Run(() => _seams.Decide(link.ResolvedUri, context));
        if (!MayAsk(confirm, link, click))
        {
            return;
        }

        if (current != confirm)
        {
            _log.LogWarning($"Link: {LogText.Clean(confirm.Path)} changed while the card was up; nothing opened.");
            _toasts.Show(ToastKind.Warning, LinkFailedToast);
            return;
        }

        IFileActions fileActions = _seams.FileActions();
        await StartDefaultAppAsync(confirm, confirm.Path, () => fileActions.OpenConfirmed(confirm.Path, confirm.IsDirectory));
    }

    /// <summary>Whether <paramref name="confirm"/> may still act: its click is the newest and its document is still shown.</summary>
    private bool MayAsk(Confirm confirm, LinkActivatedEventArgs link, int click)
    {
        if (!IsNewest(click, link))
        {
            return false;
        }

        if (!_documents.IsShown(link.DocumentPath!))
        {
            _log.LogInfo($"Link: {LogText.Clean(confirm.Path)} not asked; the document was left.");
            return false;
        }

        return true;
    }

    /// <summary>Runs <paramref name="open"/> off the UI thread; a failure is logged and shown in a toast.</summary>
    /// <param name="decision">The decision being acted on.</param>
    /// <param name="path">The path being opened, named in the log and the toast.</param>
    /// <param name="open">The file action that opens it.</param>
    private async Task StartDefaultAppAsync(LinkDecision decision, string path, Action open)
    {
        _log.LogInfo($"Link: opening {LogText.Clean(path)} in its default app.");
        try
        {
            await _seams.Run(() =>
            {
                open();
                return decision;
            });
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Link: could not open {LogText.Clean(path)} in its default app.", exception);
            _toasts.Show(ToastKind.Warning, $"Couldn't open {ToastValue.Code(Path.GetFileName(path))}");
        }
    }

    private void Refuse(Ignore ignore, LinkActivatedEventArgs link, string groupRoot)
    {
        _log.LogInfo($"Link ignored: {ignore.Reason}: {LogText.Clean(link.Href)}");
        string href = link.Href;
        (ToastKind Kind, string Text)? toast = ignore.Kind switch
        {
            IgnoreKind.Missing => (ToastKind.Warning, $"{ToastValue.Code(href)} doesn't exist"),
            IgnoreKind.Scheme => (ToastKind.Refusal, $"YAMV doesn't open {ToastValue.Code(SchemeOf(link) + ":")} links"),
            IgnoreKind.OutsideRoot => (ToastKind.Refusal, $"YAMV doesn't open links outside {ToastValue.Code(FolderName(groupRoot))}"),
            IgnoreKind.NetworkShare => (ToastKind.Refusal, "YAMV doesn't open links to network shares"),
            IgnoreKind.TrailingDotOrSpace => (
                ToastKind.Refusal,
                $"YAMV can't open {ToastValue.Code(href)}: names ending in a dot or space aren't supported"
            ),
            IgnoreKind.NotPlainPath => (ToastKind.Refusal, NotPlainPathText(href)),
            _ => null,
        };
        if (toast is not { } shown)
        {
            _log.LogError($"Link: no toast for an ignored link of kind {ignore.Kind}: {LogText.Clean(href)}");
            _toasts.Show(ToastKind.Warning, LinkFailedToast);
            return;
        }

        _toasts.Show(shown.Kind, shown.Text);
    }

    /// <summary>The link's scheme: the resolved URI's, else the href's text before its first colon.</summary>
    private static string SchemeOf(LinkActivatedEventArgs link)
    {
        if (link.ResolvedUri is { IsAbsoluteUri: true } uri)
        {
            return uri.Scheme;
        }

        int colon = link.Href.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? link.Href[..colon] : link.Href;
    }

    /// <summary>The last name in <paramref name="path"/>, a file's or a folder's; a drive or volume root is shown whole.</summary>
    private static string FolderName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;
}
