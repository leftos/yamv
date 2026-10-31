using Yamv.App.Controls;
using Yamv.App.ViewModels;
using Yamv.Core.Diagnostics;
using Yamv.Core.Links;

namespace Yamv.App.Services;

/// <summary>The documents of a window, as a <see cref="LinkOpener"/> sees them: the window's view model.</summary>
internal interface ILinkDocuments
{
    /// <summary>The root folder of the group holding <paramref name="documentPath"/>, or null when the document is not open.</summary>
    /// <param name="documentPath">The document that holds the link.</param>
    string? GroupRootOf(string documentPath);

    /// <summary>Opens <paramref name="path"/> in the window and selects it, scrolled to <paramref name="fragment"/> when given.</summary>
    /// <param name="path">The document's real path, as the link policy decided it; it need not exist.</param>
    /// <param name="fragment">The heading to scroll to, already decoded; null for none.</param>
    void OpenLinkedDocument(string path, string? fragment);
}

/// <summary>What a <see cref="LinkOpener"/> hands its work to: the browser, the file actions, the policy and the thread pool.</summary>
/// <param name="OpenInBrowser">Opens an http, https or mailto URI in the platform's browser or mail app; false when it did not.</param>
/// <param name="FileActions">Reads the window's current file actions, which open a viewable file in its default app.</param>
/// <param name="Decide">Decides what a link opens; <see cref="LinkPolicy.Decide(Uri?, LinkContext)"/> outside tests.</param>
/// <param name="Run">Runs blocking work off the UI thread; <c>Task.Run</c> outside tests.</param>
internal sealed record LinkOpenerSeams(
    Func<Uri, Task<bool>> OpenInBrowser,
    Func<IFileActions> FileActions,
    Func<Uri?, LinkContext, LinkDecision> Decide,
    Func<Func<LinkDecision>, Task<LinkDecision>> Run
);

/// <summary>
/// Turns a clicked link in a window's document into the one action its <see cref="LinkDecision"/> names, and tells the user
/// in a toast when a link opens nothing. The decision is made off the UI thread, and only the newest click acts.
/// </summary>
internal sealed class LinkOpener : IDisposable
{
    /// <summary>The toast of a link the app could not act on.</summary>
    internal const string LinkFailedToast = "Couldn't open the link; see the log";

    private readonly ILinkDocuments _documents;
    private readonly IToasts _toasts;
    private readonly IAppLog _log;
    private readonly LinkOpenerSeams _seams;
    private int _newestClick;
    private bool _disposed;

    /// <summary>Creates the link opener of one window.</summary>
    /// <param name="documents">The window's documents, which give a link its group root and open linked documents.</param>
    /// <param name="toasts">Where a link that opens nothing is shown.</param>
    /// <param name="log">Where every link and its outcome is logged.</param>
    /// <param name="seams">The browser, the file actions, the policy and the thread pool.</param>
    public LinkOpener(ILinkDocuments documents, IToasts toasts, IAppLog log, LinkOpenerSeams seams)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(toasts);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(seams);
        _documents = documents;
        _toasts = toasts;
        _log = log;
        _seams = seams;
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
            _log.LogError($"Link: could not handle {e?.Href}.", exception);
            _toasts.Show(ToastKind.Warning, LinkFailedToast);
        }
    }

    /// <summary>Decides <paramref name="link"/> off the UI thread and acts on the decision back on it.</summary>
    /// <remarks>
    /// Only a click acts. The group root is read on the UI thread from the document that holds the link, not the selection;
    /// a decision that finishes after a newer click, or after the window closed, does nothing.
    /// </remarks>
    internal async Task HandleAsync(LinkActivatedEventArgs link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.Source != LinkSource.Click)
        {
            _log.LogInfo($"Link from a {SourceName(link.Source)} ignored: {link.Href}");
            return;
        }

        if (link.DocumentPath is not string documentPath || _documents.GroupRootOf(documentPath) is not string groupRoot)
        {
            _log.LogInfo($"Link ignored: the document that held it is no longer open: {link.Href}");
            return;
        }

        int click = ++_newestClick;
        var context = new LinkContext(groupRoot, RealPath.Resolve);
        LinkDecision decision;
        try
        {
            decision = await _seams.Run(() => _seams.Decide(link.ResolvedUri, context));
        }
        catch (Exception exception)
        {
            _log.LogError($"Link: could not decide what {link.Href} opens.", exception);
            if (IsNewest(click, link))
            {
                _toasts.Show(ToastKind.Refusal, NotPlainPathText(link.Href));
            }

            return;
        }

        if (IsNewest(click, link))
        {
            await ActAsync(decision, link, groupRoot);
        }
    }

    public void Dispose() => _disposed = true;

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
            _log.LogInfo($"Link decided after the window closed; nothing opened: {link.Href}");
            return false;
        }

        if (click != _newestClick)
        {
            _log.LogInfo($"Link superseded by a newer click; nothing opened: {link.Href}");
            return false;
        }

        return true;
    }

    /// <summary>Does the one thing <paramref name="decision"/> names.</summary>
    private Task ActAsync(LinkDecision decision, LinkActivatedEventArgs link, string groupRoot)
    {
        switch (decision)
        {
            case OpenExternal external:
                return OpenExternalAsync(external, link);
            case OpenDocument document:
                _log.LogInfo($"Link: opening the document {document.Path} (linked as {document.LinkPath}).");
                _documents.OpenLinkedDocument(document.Path, document.Fragment);
                return Task.CompletedTask;
            case OpenWithDefaultApp app:
                return OpenWithDefaultAppAsync(app);
            case Confirm confirm:
                _log.LogInfo($"Link: {confirm.Path} needs the user's confirmation, which YAMV can't ask yet; nothing opened.");
                _toasts.Show(ToastKind.Refusal, $"YAMV doesn't open {ToastValue.Code(Path.GetFileName(confirm.Path))} from a link yet");
                return Task.CompletedTask;
            case Ignore ignore:
                Refuse(ignore, link, groupRoot);
                return Task.CompletedTask;
            default:
                _log.LogError($"Link: no action for a {decision.GetType().Name} decision on {link.Href}; nothing opened.");
                _toasts.Show(ToastKind.Warning, LinkFailedToast);
                return Task.CompletedTask;
        }
    }

    private async Task OpenExternalAsync(OpenExternal external, LinkActivatedEventArgs link)
    {
        if (external.DroppedFields.Count > 0)
        {
            _log.LogInfo($"Link: left out the mailto fields {string.Join(", ", external.DroppedFields)} of {link.Href}");
        }

        Uri uri = external.Uri;
        if (!uri.IsAbsoluteUri || uri.IsFile || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            _log.LogError($"Link: refused to hand {uri.OriginalString} to the browser: not an http, https or mailto URI.");
            _toasts.Show(ToastKind.Warning, LinkFailedToast);
            return;
        }

        _log.LogInfo($"Link: opening {uri.AbsoluteUri} in the browser.");
        bool opened;
        try
        {
            opened = await _seams.OpenInBrowser(uri);
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Link: the browser could not open {uri.AbsoluteUri}.", exception);
            opened = false;
        }

        if (!opened)
        {
            _log.LogWarning($"Link: the browser did not open {uri.AbsoluteUri}.");
            _toasts.Show(ToastKind.Warning, "Couldn't open the link in your browser");
        }
    }

    private async Task OpenWithDefaultAppAsync(OpenWithDefaultApp app)
    {
        _log.LogInfo($"Link: opening {app.Path} in its default app.");
        IFileActions fileActions = _seams.FileActions();
        try
        {
            await _seams.Run(() =>
            {
                fileActions.OpenViewable(app.Path);
                return app;
            });
        }
        catch (Exception exception)
        {
            _log.LogWarning($"Link: could not open {app.Path} in its default app.", exception);
            _toasts.Show(ToastKind.Warning, $"Couldn't open {ToastValue.Code(Path.GetFileName(app.Path))}");
        }
    }

    private void Refuse(Ignore ignore, LinkActivatedEventArgs link, string groupRoot)
    {
        _log.LogInfo($"Link ignored: {ignore.Reason}: {link.Href}");
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
            _log.LogError($"Link: no toast for an ignored link of kind {ignore.Kind}: {href}");
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

    /// <summary>The group folder's name; a drive or volume root is shown whole.</summary>
    private static string FolderName(string groupRoot) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(groupRoot)) is { Length: > 0 } name ? name : groupRoot;
}
