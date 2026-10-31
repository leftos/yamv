using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;
using Yamv.App.Controls;
using Yamv.App.Services;
using Yamv.App.Tests.Fakes;
using Yamv.Core.Links;

namespace Yamv.App.Tests.Services;

public sealed class LinkOpenerTests
{
    private static readonly TimeSpan Backstop = TimeSpan.FromMinutes(5);
    private static readonly string Root = P("dev", "repo");
    private static readonly string Doc = Path.Combine(Root, "docs", "a.md");
    private static readonly string OtherRoot = P("dev", "other");
    private static readonly string OtherDoc = Path.Combine(OtherRoot, "b.md");

    [Theory]
    [InlineData(LinkSource.Navigation, "navigation")]
    [InlineData(LinkSource.NewWindow, "new window")]
    public Task Navigation_and_new_window_links_open_nothing(LinkSource source, string sourceName) =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture { Decision = new OpenExternal(new Uri("https://example.com/"), []) };

            await fixture.ClickAsync(new LinkActivatedEventArgs("https://example.com/", new Uri("https://example.com/"), 0, source, Doc));

            Assert.Equal(0, fixture.Runs);
            Assert.Empty(fixture.Contexts);
            fixture.AssertNothingOpened();
            Assert.Empty(fixture.Dropped);
            Assert.Empty(fixture.Toasts.Shown);
            LogEntry entry = Assert.Single(fixture.Log.Entries);
            Assert.Equal(("info", $"Link from a {sourceName} ignored: https://example.com/"), (entry.Level, entry.Message));
        });

    [Fact]
    public Task A_file_dropped_on_the_page_opens_whatever_document_the_view_last_showed() =>
        Headless.RunAsync(async () =>
        {
            string dropped = P("elsewhere", "dropped.md");
            var uri = new Uri(dropped);
            var fixture = new Fixture { DragSignalled = true, Decision = new OpenDocument(dropped, dropped, null) };

            // WebView2 raises a new-window request for a drop; the view stamps the latest show, which need not be open.
            await fixture.ClickAsync(new LinkActivatedEventArgs(uri.AbsoluteUri, uri, 0, LinkSource.NewWindow, P("gone", "closed.md")));
            fixture.EndDropWait();

            Assert.Equal([dropped], Assert.Single(fixture.Dropped));
            Assert.Equal(0, fixture.Runs);
            fixture.AssertNothingOpened();
            Assert.Empty(fixture.Toasts.Shown);
        });

    [Fact]
    public Task A_drop_arriving_after_the_window_closed_opens_nothing() =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture { DragSignalled = true };

            await fixture.Opener.HandleAsync(NewWindow(new Uri(P("drop", "b.md"))));
            fixture.Opener.Dispose();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(fixture.Dropped);
            LogEntry entry = Assert.Single(fixture.Log.Entries);
            Assert.Contains("after the window closed", entry.Message, StringComparison.Ordinal);
        });

    [Fact]
    public Task Files_of_one_drop_join_one_batch() =>
        Headless.RunAsync(async () =>
        {
            string[] paths = [P("drop", "b.md"), P("drop", "c.txt")];
            TimeSpan almost = LinkOpener.DropBatchWait - TimeSpan.FromMilliseconds(1);
            var fixture = new Fixture { DragSignalled = true };

            await fixture.Opener.HandleAsync(NewWindow(new Uri(paths[0])));
            fixture.Advance(almost);
            await fixture.Opener.HandleAsync(NewWindow(new Uri(paths[1])));
            fixture.Advance(almost);
            Assert.Empty(fixture.Dropped);

            fixture.Advance(TimeSpan.FromMilliseconds(1));

            Assert.Equal(paths, Assert.Single(fixture.Dropped));
        });

    [Fact]
    public Task A_file_arriving_after_the_signal_ended_joins_its_batch() =>
        Headless.RunAsync(async () =>
        {
            string[] paths = [P("drop", "b.md"), P("drop", "c.txt")];
            var fixture = new Fixture { DragSignalled = true };

            await fixture.Opener.HandleAsync(NewWindow(new Uri(paths[0])));
            fixture.DragSignalled = false;
            await fixture.Opener.HandleAsync(NewWindow(new Uri(paths[1])));
            fixture.EndDropWait();

            Assert.Equal(paths, Assert.Single(fixture.Dropped));
        });

    [Fact]
    public Task A_new_window_link_after_the_batch_opened_is_ignored() =>
        Headless.RunAsync(async () =>
        {
            string dropped = P("drop", "b.md");
            var fixture = new Fixture { DragSignalled = true };

            await fixture.Opener.HandleAsync(NewWindow(new Uri(dropped)));
            fixture.EndDropWait();
            fixture.DragSignalled = false;
            await fixture.Opener.HandleAsync(NewWindow(new Uri(P("drop", "c.txt"))));
            fixture.EndDropWait();

            Assert.Equal([dropped], Assert.Single(fixture.Dropped));
            Assert.Contains(fixture.Log.Entries, entry => entry.Message.StartsWith("Link from a new window ignored", StringComparison.Ordinal));
        });

    [Fact]
    public Task Three_dropped_files_open_as_one_batch_in_order() =>
        Headless.RunAsync(async () =>
        {
            string[] paths = [P("drop", "b.md"), P("drop", "c.txt"), P("drop", "a.md")];
            var fixture = new Fixture { DragSignalled = true };

            foreach (string path in paths)
            {
                await fixture.Opener.HandleAsync(NewWindow(new Uri(path)));
            }

            Assert.Empty(fixture.Dropped);
            fixture.EndDropWait();

            Assert.Equal(paths, Assert.Single(fixture.Dropped));
        });

    [Fact]
    public Task A_new_window_file_link_with_no_drag_signal_is_ignored() =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture();

            await fixture.ClickAsync(NewWindow(new Uri(P("drop", "b.md"))));
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(fixture.Dropped);
            fixture.AssertNothingOpened();
            Assert.Equal("info", Assert.Single(fixture.Log.Entries).Level);
        });

    [Fact]
    public Task An_http_new_window_link_during_a_drag_is_ignored() =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture { DragSignalled = true };

            await fixture.ClickAsync(NewWindow(new Uri("https://example.com/page.md")));
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(fixture.Dropped);
            fixture.AssertNothingOpened();
            Assert.Empty(fixture.Toasts.Shown);
        });

    [Fact]
    public Task A_navigation_link_during_a_drag_is_ignored() =>
        Headless.RunAsync(async () =>
        {
            var uri = new Uri(P("drop", "b.md"));
            var fixture = new Fixture { DragSignalled = true };

            await fixture.ClickAsync(new LinkActivatedEventArgs(uri.AbsoluteUri, uri, 0, LinkSource.Navigation, Doc));
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(fixture.Dropped);
            fixture.AssertNothingOpened();
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_link_whose_document_is_no_longer_open_is_dropped(bool noDocument) =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture { Decision = new OpenExternal(new Uri("https://example.com/"), []) };
            string? documentPath = noDocument ? null : Path.Combine(Root, "closed.md");

            await fixture.ClickAsync(Click("https://example.com/", new Uri("https://example.com/"), documentPath));

            Assert.Equal(0, fixture.Runs);
            fixture.AssertNothingOpened();
            Assert.Empty(fixture.Toasts.Shown);
            LogEntry entry = Assert.Single(fixture.Log.Entries);
            Assert.Equal("info", entry.Level);
            Assert.StartsWith("Link ignored: ", entry.Message, StringComparison.Ordinal);
        });

    [Fact]
    public Task A_link_is_decided_with_the_root_of_the_clicked_document_not_the_selection() =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture { Decision = new Ignore(IgnoreKind.Missing, "the file does not exist") };
            var uri = new Uri(Path.Combine(OtherRoot, "c.md"));

            await fixture.ClickAsync(Click("c.md", uri, OtherDoc));

            (Uri? decided, LinkContext context) = Assert.Single(fixture.Contexts);
            Assert.Same(uri, decided);
            Assert.Equal(OtherRoot, context.GroupRoot);
            fixture.AssertNothingOpened();
        });

    [Fact]
    public Task Decide_runs_through_the_runner_not_inline() =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture { Decision = new OpenExternal(new Uri("https://example.com/"), []) };

            Task handled = fixture.Opener.HandleAsync(Click("https://example.com/", new Uri("https://example.com/"), Doc));

            Assert.Empty(fixture.Contexts);
            Assert.Equal(1, fixture.Pending);
            Assert.False(handled.IsCompleted);
            await fixture.DrainAsync(handled);
            Assert.Single(fixture.Contexts);
            Assert.Single(fixture.Browser);
        });

    [Fact]
    public Task A_newer_click_supersedes_a_decision_still_running() =>
        Headless.RunAsync(async () =>
        {
            var first = new Uri("https://example.com/first");
            var second = new Uri("https://example.com/second");
            var fixture = new Fixture { DecideBy = uri => new OpenExternal(uri!, []) };

            Task firstHandled = fixture.Opener.HandleAsync(Click("first", first, Doc));
            Task secondHandled = fixture.Opener.HandleAsync(Click("second", second, Doc));
            await fixture.DrainAsync(firstHandled);
            await fixture.DrainAsync(secondHandled);

            Assert.Equal([second], fixture.Browser);
            Assert.Empty(fixture.Toasts.Shown);
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "info" && entry.Message.Contains("superseded", StringComparison.Ordinal));
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level != "info");
        });

    [Fact]
    public Task A_decision_finishing_after_dispose_does_nothing() =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture { Decision = new Ignore(IgnoreKind.Missing, "the file does not exist") };

            Task handled = fixture.Opener.HandleAsync(Click("gone.md", new Uri(Path.Combine(Root, "gone.md")), Doc));
            fixture.Opener.Dispose();
            await fixture.DrainAsync(handled);

            fixture.AssertNothingOpened();
            Assert.Empty(fixture.Toasts.Shown);
            LogEntry entry = Assert.Single(fixture.Log.Entries);
            Assert.Equal("info", entry.Level);
            Assert.Contains("after the window closed", entry.Message, StringComparison.Ordinal);
        });

    [Fact]
    public Task An_http_link_opens_the_browser_once_with_the_decided_uri() =>
        Headless.RunAsync(async () =>
        {
            var decided = new Uri("https://example.com/a?b=c%26d");
            var fixture = new Fixture { Decision = new OpenExternal(decided, []) };

            await fixture.ClickAsync(Click("//example.com/a?b=c%26d", new Uri("https://example.com/a?b=c%26d#x"), Doc));

            Assert.Same(decided, Assert.Single(fixture.Browser));
            Assert.Empty(fixture.Files.Viewed);
            Assert.Empty(fixture.Documents.Opened);
            Assert.Empty(fixture.Toasts.Shown);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level != "info");
        });

    [Fact]
    public Task A_mailto_with_dropped_fields_logs_them() =>
        Headless.RunAsync(async () =>
        {
            var decided = new Uri("mailto:a@example.com?subject=hi");
            var fixture = new Fixture { Decision = new OpenExternal(decided, ["attach", "x-extra"]) };

            await fixture.ClickAsync(Click("mailto:a@example.com?subject=hi&attach=c:/x", new Uri("mailto:a@example.com"), Doc));

            Assert.Same(decided, Assert.Single(fixture.Browser));
            Assert.Contains(
                fixture.Log.Entries,
                entry => entry.Level == "info" && entry.Message.Contains("attach, x-extra", StringComparison.Ordinal)
            );
            Assert.Empty(fixture.Toasts.Shown);
            Assert.Empty(fixture.Files.Viewed);
            Assert.Empty(fixture.Documents.Opened);
        });

    [Theory]
    [InlineData("file:///C:/tools/x.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("x.html")]
    public Task A_forged_file_OpenExternal_never_reaches_the_browser(string forged) =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture { Decision = new OpenExternal(new Uri(forged, UriKind.RelativeOrAbsolute), []) };

            await fixture.ClickAsync(Click("x", new Uri("https://example.com/"), Doc));

            fixture.AssertNothingOpened();
            Assert.Equal((ToastKind.Warning, LinkOpener.LinkFailedToast), Assert.Single(fixture.Toasts.Shown));
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "error");
        });

    [Fact]
    public Task A_non_web_uri_handed_to_the_browser_toasts() =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture { Decision = new OpenExternal(new Uri("javascript:alert(1)"), []) };

            await fixture.ClickAsync(Click("x", new Uri("https://example.com/"), Doc));

            Assert.Empty(fixture.Browser);
            Assert.Equal((ToastKind.Warning, LinkOpener.LinkFailedToast), Assert.Single(fixture.Toasts.Shown));
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "error");
        });

    [Fact]
    public Task A_link_that_throws_while_opening_toasts() =>
        Headless.RunAsync(async () =>
        {
            string target = Path.Combine(Root, "docs", "b.md");
            var fixture = new Fixture { Decision = new OpenDocument(target, "B.md", null) };
            fixture.Documents.OpenFailure = new IOException("The window went away.");

            fixture.Opener.OnLinkActivated(null, Click("B.md", new Uri(target), Doc));
            Dispatcher.UIThread.RunJobs();
            fixture.RunOne();

            Assert.Empty(fixture.Documents.Opened);
            Assert.Equal((ToastKind.Warning, LinkOpener.LinkFailedToast), Assert.Single(fixture.Toasts.Shown));
            LogEntry entry = Assert.Single(fixture.Log.Entries, logged => logged.Level == "error");
            Assert.IsType<IOException>(entry.Exception);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_browser_launch_that_fails_or_throws_toasts_and_does_not_crash(bool throws) =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture
            {
                Decision = new OpenExternal(new Uri("https://example.com/"), []),
                Browse = throws ? () => throw new InvalidOperationException("No browser.") : () => Task.FromResult(false),
            };

            await fixture.ClickAsync(Click("https://example.com/", new Uri("https://example.com/"), Doc));

            Assert.Single(fixture.Browser);
            Assert.Equal((ToastKind.Warning, "Couldn't open the link in your browser"), Assert.Single(fixture.Toasts.Shown));
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "warning");
            Assert.Empty(fixture.Files.Viewed);
            Assert.Empty(fixture.Documents.Opened);
        });

    [Fact]
    public Task A_document_link_opens_the_document_with_its_fragment() =>
        Headless.RunAsync(async () =>
        {
            string target = Path.Combine(Root, "docs", "b.md");
            var fixture = new Fixture { Decision = new OpenDocument(target, Path.Combine(Root, "docs", "B.md"), "set up") };

            await fixture.ClickAsync(Click("B.md#set%20up", new Uri(target + "#set%20up"), Doc));

            Assert.Equal((target, "set up"), Assert.Single(fixture.Documents.Opened));
            Assert.Empty(fixture.Browser);
            Assert.Empty(fixture.Files.Viewed);
            Assert.Empty(fixture.Toasts.Shown);
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level != "info");
        });

    [Fact]
    public Task A_viewable_file_opens_with_the_default_app() =>
        Headless.RunAsync(async () =>
        {
            string image = Path.Combine(Root, "docs", "chart.png");
            var fixture = new Fixture { Decision = new OpenWithDefaultApp(image) };

            await fixture.ClickAsync(Click("chart.png", new Uri(image), Doc));

            Assert.Equal([image], fixture.Files.Viewed);
            Assert.Equal(2, fixture.Runs);
            Assert.Empty(fixture.Files.Opened);
            Assert.Empty(fixture.Browser);
            Assert.Empty(fixture.Documents.Opened);
            Assert.Empty(fixture.Toasts.Shown);
        });

    [Fact]
    public Task A_default_app_open_that_throws_toasts() =>
        Headless.RunAsync(async () =>
        {
            string image = Path.Combine(Root, "docs", "chart.png");
            var fixture = new Fixture { Decision = new OpenWithDefaultApp(image) };
            fixture.Files.ViewFailure = new IOException("No app.");

            await fixture.ClickAsync(Click("chart.png", new Uri(image), Doc));

            Assert.Equal((ToastKind.Warning, "Couldn't open `chart.png`"), Assert.Single(fixture.Toasts.Shown));
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "warning" && entry.Exception is IOException);
            fixture.AssertNothingOpened();
        });

    [Fact]
    public Task A_link_of_an_unlisted_type_asks_before_opening() =>
        Headless.RunAsync(async () =>
        {
            string script = Path.Combine(Root, "tools", "setup.ps1");
            var fixture = new Fixture { Decision = new Confirm(script, IsDirectory: false) };

            Task handled = fixture.ClickUpToTheCard(script);

            (string title, string body, _) = Assert.Single(fixture.Cards);
            Assert.Equal("Open setup.ps1?", title);
            Assert.Equal(ConfirmText.For(script, "setup.ps1", isFolder: false, OperatingSystem.IsMacOS()).Body, body);
            Assert.False(handled.IsCompleted);
            fixture.AssertNothingOpened();
            Assert.Empty(fixture.Toasts.Shown);
            Assert.Contains(fixture.Log.Entries, entry => entry.Message == $"Link: asking before opening {script}.");
            fixture.Answer(open: false);
            await fixture.DrainAsync(handled);
        });

    [Fact]
    public Task Answering_open_opens_the_checked_path() =>
        Headless.RunAsync(async () =>
        {
            string script = Path.Combine(Root, "tools", "setup.ps1");
            var fixture = new Fixture { Decision = new Confirm(script, IsDirectory: false) };

            Task handled = fixture.ClickUpToTheCard(script);
            fixture.Answer(open: true);
            await fixture.DrainAsync(handled);

            Assert.Equal([script], fixture.Files.Confirmed);
            Assert.Empty(fixture.Files.Viewed);
            Assert.Empty(fixture.Toasts.Shown);
        });

    [Fact]
    public Task A_folder_link_opens_as_a_folder() =>
        Headless.RunAsync(async () =>
        {
            string folder = Path.Combine(Root, "assets");
            var fixture = new Fixture { Decision = new Confirm(folder, IsDirectory: true) };

            Task handled = fixture.ClickUpToTheCard(folder);
            fixture.Answer(open: true);
            await fixture.DrainAsync(handled);

            Assert.Equal([folder], fixture.Files.Confirmed);
            Assert.Equal([true], fixture.Files.ConfirmedAsDirectory);
        });

    [Fact]
    public Task Cancelling_opens_nothing_and_shows_no_toast() =>
        Headless.RunAsync(async () =>
        {
            string script = Path.Combine(Root, "tools", "setup.ps1");
            var fixture = new Fixture { Decision = new Confirm(script, IsDirectory: false) };

            Task handled = fixture.ClickUpToTheCard(script);
            fixture.Answer(open: false);
            await fixture.DrainAsync(handled);

            fixture.AssertNothingOpened();
            Assert.Empty(fixture.Toasts.Shown);
            Assert.Contains(fixture.Log.Entries, entry => entry.Message == $"Link: {script} not opened; the user cancelled.");
        });

    [Fact]
    public Task A_newer_click_while_the_card_is_up_opens_nothing_from_the_old_one() =>
        Headless.RunAsync(async () =>
        {
            string script = Path.Combine(Root, "tools", "setup.ps1");
            var web = new Uri("https://example.com/");
            var fixture = new Fixture { DecideBy = uri => uri == web ? new OpenExternal(web, []) : new Confirm(script, IsDirectory: false) };

            Task handled = fixture.ClickUpToTheCard(script);
            await fixture.ClickAsync(Click("https://example.com/", web, Doc));
            fixture.Answer(open: true);
            await fixture.DrainAsync(handled);

            Assert.Equal([web], fixture.Browser);
            Assert.Empty(fixture.Files.Confirmed);
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "info" && entry.Message.Contains("superseded", StringComparison.Ordinal));
        });

    [Fact]
    public Task A_failed_confirmed_open_toasts() =>
        Headless.RunAsync(async () =>
        {
            string script = Path.Combine(Root, "tools", "setup.ps1");
            var fixture = new Fixture { Decision = new Confirm(script, IsDirectory: false) };
            fixture.Files.ConfirmFailure = new IOException("No app.");

            Task handled = fixture.ClickUpToTheCard(script);
            fixture.Answer(open: true);
            await fixture.DrainAsync(handled);

            Assert.Equal((ToastKind.Warning, "Couldn't open `setup.ps1`"), Assert.Single(fixture.Toasts.Shown));
            Assert.Contains(fixture.Log.Entries, entry => entry.Level == "warning" && entry.Exception is IOException);
            fixture.AssertNothingOpened();
        });

    [Fact]
    public Task A_name_with_a_control_character_is_sanitised_in_the_card() =>
        Headless.RunAsync(async () =>
        {
            string script = Path.Combine(Root, "tools", "set\u202Eup.ps1");
            var fixture = new Fixture { Decision = new Confirm(script, IsDirectory: false) };

            Task handled = fixture.ClickUpToTheCard(script);

            (string title, _, _) = Assert.Single(fixture.Cards);
            Assert.Equal("Open set\uFFFDup.ps1?", title);
            fixture.Answer(open: false);
            await fixture.DrainAsync(handled);
        });

    [Fact]
    public Task A_double_extension_shows_its_real_type() =>
        Headless.RunAsync(async () =>
        {
            string file = Path.Combine(Root, "tools", "report.pdf.exe");
            var fixture = new Fixture { Decision = new Confirm(file, IsDirectory: false) };

            Task handled = fixture.ClickUpToTheCard(file);

            (string title, string body, _) = Assert.Single(fixture.Cards);
            Assert.Equal("Open report.pdf.exe?", title);
            Assert.StartsWith("It's a program (.exe).", body, StringComparison.Ordinal);
            fixture.Answer(open: false);
            await fixture.DrainAsync(handled);
        });

    [Fact]
    public Task A_document_switch_before_the_card_shows_asks_nothing() =>
        Headless.RunAsync(async () =>
        {
            string script = Path.Combine(Root, "tools", "setup.ps1");
            var fixture = new Fixture { Decision = new Confirm(script, IsDirectory: false) };

            Task handled = fixture.Opener.HandleAsync(Click("link", new Uri(script), Doc));
            fixture.Documents.Shown = false;
            await fixture.DrainAsync(handled);

            Assert.Empty(fixture.Cards);
            fixture.AssertNothingOpened();
            Assert.Contains(fixture.Log.Entries, entry => entry.Message == $"Link: {script} not asked; the document was left.");
        });

    [Fact]
    public Task A_file_changed_while_the_card_was_up_opens_nothing() =>
        Headless.RunAsync(async () =>
        {
            string script = Path.Combine(Root, "tools", "setup.ps1");
            LinkDecision current = new Confirm(script, IsDirectory: false);
            var fixture = new Fixture { DecideBy = _ => current };

            Task handled = fixture.ClickUpToTheCard(script);
            current = new Confirm(script, IsDirectory: true);
            fixture.Answer(open: true);
            await fixture.DrainAsync(handled);

            fixture.AssertNothingOpened();
            Assert.Equal((ToastKind.Warning, LinkOpener.LinkFailedToast), Assert.Single(fixture.Toasts.Shown));
            Assert.Contains(fixture.Log.Entries, entry => entry.Message == $"Link: {script} changed while the card was up; nothing opened.");
        });

    [Theory]
    [InlineData(IgnoreKind.Missing, "gone.md", "file:///C:/dev/repo/gone.md", "repo", ToastKind.Warning, "`gone.md` doesn't exist")]
    [InlineData(IgnoreKind.Missing, "a`b\nc.md", null, "repo", ToastKind.Warning, "`a\u02CBb\uFFFDc.md` doesn't exist")]
    [InlineData(IgnoreKind.Scheme, "ms-settings:privacy", "ms-settings:privacy", "repo", ToastKind.Refusal, "YAMV doesn't open `ms-settings:` links")]
    [InlineData(IgnoreKind.Scheme, "ftp:x", null, "repo", ToastKind.Refusal, "YAMV doesn't open `ftp:` links")]
    [InlineData(IgnoreKind.OutsideRoot, "../../x.md", "file:///C:/x.md", "repo", ToastKind.Refusal, "YAMV doesn't open links outside `repo`")]
    [InlineData(IgnoreKind.OutsideRoot, "../x.md", "file:///C:/x.md", "/", ToastKind.Refusal, "YAMV doesn't open links outside `/`")]
    [InlineData(IgnoreKind.NetworkShare, "file://server/share/x.md", null, "repo", ToastKind.Refusal, "YAMV doesn't open links to network shares")]
    [InlineData(
        IgnoreKind.TrailingDotOrSpace,
        "notes./a.md",
        null,
        "repo",
        ToastKind.Refusal,
        "YAMV can't open `notes./a.md`: names ending in a dot or space aren't supported"
    )]
    [InlineData(
        IgnoreKind.NotPlainPath,
        "\\\\?\\C:\\x.md",
        null,
        "repo",
        ToastKind.Refusal,
        "YAMV doesn't open `\\\\?\\C:\\x.md`: not a plain local path"
    )]
    public Task An_ignored_link_toasts_the_owners_text_for_its_kind(
        IgnoreKind kind,
        string href,
        string? resolved,
        string root,
        ToastKind toastKind,
        string text
    ) =>
        Headless.RunAsync(async () =>
        {
            string groupRoot = root == "repo" ? Root : root;
            string document = Path.Combine(groupRoot, "a.md");
            var fixture = new Fixture { Decision = new Ignore(kind, "the policy said no") };
            fixture.Documents.Roots[document] = groupRoot;

            await fixture.ClickAsync(Click(href, resolved is null ? null : new Uri(resolved), document));

            (ToastKind shownKind, string shownText) = Assert.Single(fixture.Toasts.Shown);
            Assert.Equal((toastKind, text), (shownKind, shownText));
            Assert.Equal(kind == IgnoreKind.NetworkShare ? 0 : 2, shownText.Count(character => character == '`'));
            fixture.AssertNothingOpened();
            LogEntry entry = Assert.Single(fixture.Log.Entries);
            Assert.Equal("info", entry.Level);
            Assert.Contains("the policy said no", entry.Message, StringComparison.Ordinal);
        });

    [Fact]
    public Task A_share_link_never_reaches_any_sink() =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture { DecideBy = null };

            await fixture.ClickAsync(Click("//server/share/x.md", new Uri("file://server/share/x.md"), Doc));

            fixture.AssertNothingOpened();
            Assert.Equal((ToastKind.Refusal, "YAMV doesn't open links to network shares"), Assert.Single(fixture.Toasts.Shown));
            Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Level != "info");
        });

    [Fact]
    public Task A_decision_that_throws_toasts_not_a_plain_local_path() =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture { DecideBy = _ => throw new IOException("The disk went away.") };

            await fixture.ClickAsync(Click("a.md", new Uri(Path.Combine(Root, "a.md")), Doc));

            fixture.AssertNothingOpened();
            Assert.Equal((ToastKind.Refusal, "YAMV doesn't open `a.md`: not a plain local path"), Assert.Single(fixture.Toasts.Shown));
            LogEntry entry = Assert.Single(fixture.Log.Entries);
            Assert.Equal("error", entry.Level);
            Assert.IsType<IOException>(entry.Exception);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_decision_that_throws_after_a_newer_click_or_close_logs_without_a_toast(bool close) =>
        Headless.RunAsync(async () =>
        {
            var first = new Uri(Path.Combine(Root, "a.md"));
            var fixture = new Fixture
            {
                DecideBy = uri => uri == first ? throw new IOException("The disk went away.") : new OpenExternal(new Uri("https://example.com/"), []),
            };

            Task handled = fixture.Opener.HandleAsync(Click("a.md", first, Doc));
            if (close)
            {
                fixture.Opener.Dispose();
            }
            else
            {
                _ = fixture.Opener.HandleAsync(Click("https://example.com/", new Uri("https://example.com/"), Doc));
            }

            await fixture.DrainAsync(handled);

            fixture.AssertNothingOpened();
            Assert.Empty(fixture.Toasts.Shown);
            LogEntry error = Assert.Single(fixture.Log.Entries, entry => entry.Level == "error");
            Assert.IsType<IOException>(error.Exception);
            Assert.Contains(
                fixture.Log.Entries,
                entry => entry.Level == "info" && entry.Message.Contains(close ? "after the window closed" : "superseded", StringComparison.Ordinal)
            );
        });

    [Fact]
    public Task A_decision_of_an_unknown_type_logs_an_error_and_opens_nothing() =>
        Headless.RunAsync(async () =>
        {
            var fixture = new Fixture { Decision = new UnknownDecision() };

            await fixture.ClickAsync(Click("a.md", new Uri(Path.Combine(Root, "a.md")), Doc));

            fixture.AssertNothingOpened();
            Assert.Equal((ToastKind.Warning, LinkOpener.LinkFailedToast), Assert.Single(fixture.Toasts.Shown));
            LogEntry entry = Assert.Single(fixture.Log.Entries);
            Assert.Equal("error", entry.Level);
            Assert.Contains(nameof(UnknownDecision), entry.Message, StringComparison.Ordinal);
        });

    [Fact]
    public Task A_handler_called_with_no_link_logs_and_does_not_throw() =>
        Headless.RunAsync(() =>
        {
            var fixture = new Fixture { Decision = new OpenExternal(new Uri("https://example.com/"), []) };

            fixture.Opener.OnLinkActivated(null, null!);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, fixture.Runs);
            fixture.AssertNothingOpened();
            Assert.Equal((ToastKind.Warning, LinkOpener.LinkFailedToast), Assert.Single(fixture.Toasts.Shown));
            LogEntry entry = Assert.Single(fixture.Log.Entries);
            Assert.Equal("error", entry.Level);
            Assert.IsType<ArgumentNullException>(entry.Exception);
            return Task.CompletedTask;
        });

    /// <summary>A decision the opener does not know.</summary>
    private sealed record UnknownDecision : LinkDecision;

    private static LinkActivatedEventArgs Click(string href, Uri? resolved, string? documentPath) =>
        new(href, resolved, 0, LinkSource.Click, documentPath);

    private static LinkActivatedEventArgs NewWindow(Uri uri) => new(uri.AbsoluteUri, uri, 0, LinkSource.NewWindow, Doc);

    private static string P(params string[] parts) => Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(parts));

    /// <summary>The documents of a window: which group root each open document has, and every linked document opened.</summary>
    private sealed class FakeLinkDocuments : ILinkDocuments
    {
        public Dictionary<string, string> Roots { get; } = new(StringComparer.OrdinalIgnoreCase) { [Doc] = Root, [OtherDoc] = OtherRoot };

        public List<(string Path, string? Fragment)> Opened { get; } = [];

        /// <summary>When set, <see cref="OpenLinkedDocument"/> throws it instead of opening the document.</summary>
        public Exception? OpenFailure { get; set; }

        /// <summary>What <see cref="IsShown"/> answers.</summary>
        public bool Shown { get; set; } = true;

        public string? GroupRootOf(string documentPath) => Roots.GetValueOrDefault(documentPath);

        public bool IsShown(string documentPath) => Shown;

        public void OpenLinkedDocument(string path, string? fragment)
        {
            if (OpenFailure is { } failure)
            {
                throw failure;
            }

            Opened.Add((path, fragment));
        }
    }

    /// <summary>
    /// A link opener over fakes. Its runner holds each piece of work until the test runs it, oldest first; the decider answers
    /// <see cref="Decision"/>, or <see cref="DecideBy"/> when set, or the real policy when that is null.
    /// </summary>
    private sealed class Fixture
    {
        private readonly Queue<(Func<LinkDecision> Work, TaskCompletionSource<LinkDecision> Done)> _pending = new();

        public Fixture()
        {
            Opener = new LinkOpener(
                Documents,
                Toasts,
                Log,
                new LinkOpenerSeams(
                    uri =>
                    {
                        Browser.Add(uri);
                        return Browse();
                    },
                    () => Files,
                    (uri, context) =>
                    {
                        Contexts.Add((uri, context));
                        return DecideBy is null ? LinkPolicy.Decide(uri, context) : DecideBy(uri);
                    },
                    work =>
                    {
                        Runs++;
                        var done = new TaskCompletionSource<LinkDecision>();
                        _pending.Enqueue((work, done));
                        return done.Task;
                    },
                    Time,
                    (title, body) =>
                    {
                        var answer = new TaskCompletionSource<bool>();
                        Cards.Add((title, body, answer));
                        return answer.Task;
                    }
                ),
                new LinkDropRoute(() => DragSignalled, Dropped.Add)
            );
            DecideBy = _ => Decision!;
        }

        /// <summary>Every confirm card shown and the source of its answer, oldest first.</summary>
        public List<(string Title, string Body, TaskCompletionSource<bool> Answer)> Cards { get; } = [];

        /// <summary>Clicks a link to <paramref name="path"/> and runs the decision, leaving the card up.</summary>
        public Task ClickUpToTheCard(string path)
        {
            Task handled = Opener.HandleAsync(Click("link", new Uri(path), Doc));
            RunOne();
            return handled;
        }

        /// <summary>Answers the card shown last, and lets the UI thread take the answer.</summary>
        public void Answer(bool open)
        {
            Cards[^1].Answer.SetResult(open);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>What the drag signal answers.</summary>
        public bool DragSignalled { get; set; }

        /// <summary>The clock a drop's batch waits on.</summary>
        public FakeTimeProvider Time { get; } = new();

        public void Advance(TimeSpan by)
        {
            Time.Advance(by);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Lets a queued drop's wait run out, so its batch is handed over.</summary>
        public void EndDropWait() => Advance(LinkOpener.DropBatchWait);

        /// <summary>Every batch of dropped paths handed to the window, oldest first.</summary>
        public List<IReadOnlyList<string>> Dropped { get; } = [];

        public LinkDecision? Decision { get; init; }

        public Func<Uri?, LinkDecision>? DecideBy { get; init; }

        /// <summary>What the browser answers a launch with.</summary>
        public Func<Task<bool>> Browse { get; init; } = () => Task.FromResult(true);

        public LinkOpener Opener { get; }

        public FakeLinkDocuments Documents { get; } = new();

        public RecordingToasts Toasts { get; } = new();

        public RecordingAppLog Log { get; } = new();

        public FakeFileActions Files { get; } = new();

        public List<Uri> Browser { get; } = [];

        public List<(Uri? Uri, LinkContext Context)> Contexts { get; } = [];

        /// <summary>How many pieces of work were handed to the runner.</summary>
        public int Runs { get; private set; }

        /// <summary>How many pieces of work the runner holds.</summary>
        public int Pending => _pending.Count;

        public async Task ClickAsync(LinkActivatedEventArgs link) => await DrainAsync(Opener.HandleAsync(link));

        /// <summary>Runs held work, oldest first, until <paramref name="handled"/> completes.</summary>
        public async Task DrainAsync(Task handled)
        {
            while (!handled.IsCompleted && _pending.TryDequeue(out (Func<LinkDecision> Work, TaskCompletionSource<LinkDecision> Done) next))
            {
                try
                {
                    next.Done.SetResult(next.Work());
                }
                catch (Exception exception)
                {
                    next.Done.SetException(exception);
                }

                Dispatcher.UIThread.RunJobs();
            }

            await handled.WaitAsync(Backstop, TestContext.Current.CancellationToken);
        }

        /// <summary>Runs one held piece of work, as the runner would, and lets the UI thread finish what it starts.</summary>
        public void RunOne()
        {
            Assert.True(_pending.TryDequeue(out (Func<LinkDecision> Work, TaskCompletionSource<LinkDecision> Done) next));
            try
            {
                next.Done.SetResult(next.Work());
            }
            catch (Exception exception)
            {
                next.Done.SetException(exception);
            }

            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Asserts that no browser, default app or document was opened.</summary>
        public void AssertNothingOpened()
        {
            Assert.Empty(Browser);
            Assert.Empty(Files.Viewed);
            Assert.Empty(Files.Confirmed);
            Assert.Empty(Files.Opened);
            Assert.Empty(Documents.Opened);
        }
    }
}
