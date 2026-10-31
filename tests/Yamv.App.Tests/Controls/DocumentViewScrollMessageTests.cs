using System.Text.Json;
using Avalonia.Threading;
using Yamv.App.Controls;
using Yamv.App.Tests.Fakes;
using Yamv.Core.Rendering;
using Yamv.Core.Session;

namespace Yamv.App.Tests.Controls;

public sealed class DocumentViewScrollMessageTests
{
    private static PageMessage Parse(string body) =>
        JsonSerializer.Deserialize(body, PageJsonContext.Default.PageMessage) ?? throw new InvalidOperationException("no message");

    private static DocumentShow Show(string path) =>
        new(new RenderedDocument("<p>x</p>", null, false, false), path, Path.GetDirectoryName(path) ?? path, 0, null, null, null);

    [Fact]
    public void A_scroll_message_carries_its_fraction_and_anchor_to_ScrollChanged()
    {
        PageMessage message = Parse("""{"type":"scroll","value":0.25,"line":42,"offset":0.35,"generation":3}""");

        Assert.Equal(new ScrollPosition(0.25, new ScrollAnchor(42, 0.35)), DocumentView.ScrollPositionOf(message));
    }

    [Fact]
    public void A_scroll_message_without_a_line_has_no_anchor()
    {
        PageMessage message = Parse("""{"type":"scroll","value":0.5,"line":null,"offset":null,"generation":3}""");

        Assert.Equal(new ScrollPosition(0.5, null), DocumentView.ScrollPositionOf(message));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    public void A_scroll_message_whose_line_is_no_32_bit_whole_number_keeps_its_fraction_without_an_anchor(string line)
    {
        PageMessage message = Parse($$"""{"type":"scroll","value":0.75,"line":{{line}},"offset":0.2,"generation":3}""");

        Assert.Equal(new ScrollPosition(0.75, null), DocumentView.ScrollPositionOf(message));
    }

    [Fact]
    public void An_offset_outside_0_to_1_is_clamped_and_a_missing_one_is_0()
    {
        PageMessage over = Parse("""{"type":"scroll","value":0.5,"line":3,"offset":1.5,"generation":3}""");
        PageMessage missing = Parse("""{"type":"scroll","value":0.5,"line":3,"generation":3}""");

        Assert.Equal(new ScrollAnchor(3, 1), DocumentView.ScrollPositionOf(over)?.Anchor);
        Assert.Equal(new ScrollAnchor(3, 0), DocumentView.ScrollPositionOf(missing)?.Anchor);
    }

    [Fact]
    public void A_scroll_message_without_a_value_is_no_scroll()
    {
        PageMessage message = Parse("""{"type":"scroll","line":4,"generation":3}""");

        Assert.Null(DocumentView.ScrollPositionOf(message));
    }

    [Fact]
    public Task Handling_a_scroll_message_raises_ScrollChanged() =>
        Headless.RunAsync(() =>
        {
            var view = new DocumentView(new RecordingAppLog(), new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);
            List<ScrollPosition> raised = [];
            view.ScrollChanged += (_, position) => raised.Add(position);

            view.Handle(Parse("""{"type":"scroll","value":0.25,"line":1.5,"offset":0.4,"generation":0}"""));
            view.Handle(Parse("""{"type":"scroll","value":0.5,"line":8,"offset":0.4,"generation":0}"""));

            Assert.Equal([new ScrollPosition(0.25, null), new ScrollPosition(0.5, new ScrollAnchor(8, 0.4))], raised);
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_drag_message_raises_the_drag_event() =>
        Headless.RunAsync(() =>
        {
            var view = new DocumentView(new RecordingAppLog(), new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);
            List<bool> raised = [];
            view.DragOverChanged += (_, over) => raised.Add(over);

            view.Handle(Parse("""{"type":"drag","over":true}"""));

            Assert.Equal([true], raised);
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_drag_left_message_raises_it_false() =>
        Headless.RunAsync(() =>
        {
            var view = new DocumentView(new RecordingAppLog(), new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);
            List<bool> raised = [];
            view.DragOverChanged += (_, over) => raised.Add(over);

            view.Handle(Parse("""{"type":"drag","over":false}"""));

            Assert.Equal([false], raised);
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_click_link_carries_source_click_and_the_document_path() =>
        Headless.RunAsync(() =>
        {
            var view = new DocumentView(new RecordingAppLog(), new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);
            List<LinkActivatedEventArgs> raised = [];
            view.LinkActivated += (_, link) => raised.Add(link);
            string document = Path.Combine(Path.GetTempPath(), "yamv-link-doc", "a.md");
            _ = view.ShowAsync(Show(document));

            view.Handle(Parse("""{"type":"link","href":"other.md","button":1,"generation":1}"""));
            Dispatcher.UIThread.RunJobs();

            LinkActivatedEventArgs link = Assert.Single(raised);
            Assert.Equal(LinkSource.Click, link.Source);
            Assert.Equal(document, link.DocumentPath);
            Assert.Equal("other.md", link.Href);
            Assert.Equal(1, link.Button);
            Assert.Equal(new Uri(Path.Combine(Path.GetTempPath(), "yamv-link-doc", "other.md")), link.ResolvedUri);
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_viewport_report_for_the_shown_render_sets_its_width_and_raises_once_per_change() =>
        Headless.RunAsync(() =>
        {
            var view = new DocumentView(new RecordingAppLog(), new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);
            int raised = 0;
            view.ViewportWidthChanged += (_, _) => raised++;
            _ = view.ShowAsync(Show(Path.Combine(Path.GetTempPath(), "yamv-viewport-doc", "a.md")));

            view.Handle(Parse("""{"type":"viewport","value":995,"generation":1}"""));
            view.Handle(Parse("""{"type":"viewport","value":995,"generation":1}"""));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(995, view.ViewportWidth);
            Assert.Equal(1, raised);
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_viewport_report_from_another_render_or_without_one_is_dropped() =>
        Headless.RunAsync(() =>
        {
            var view = new DocumentView(new RecordingAppLog(), new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);
            int raised = 0;
            view.ViewportWidthChanged += (_, _) => raised++;
            _ = view.ShowAsync(Show(Path.Combine(Path.GetTempPath(), "yamv-viewport-doc", "a.md")));

            view.Handle(Parse("""{"type":"viewport","value":995,"generation":0}"""));
            view.Handle(Parse("""{"type":"viewport","value":995}"""));
            Dispatcher.UIThread.RunJobs();

            Assert.Null(view.ViewportWidth);
            Assert.Equal(0, raised);
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_new_show_clears_the_viewport_width_and_drops_the_older_render_s_late_report() =>
        Headless.RunAsync(() =>
        {
            var view = new DocumentView(new RecordingAppLog(), new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);
            string folder = Path.Combine(Path.GetTempPath(), "yamv-viewport-doc");
            _ = view.ShowAsync(Show(Path.Combine(folder, "a.md")));
            view.Handle(Parse("""{"type":"viewport","value":995,"generation":1}"""));
            int raised = 0;
            view.ViewportWidthChanged += (_, _) => raised++;

            _ = view.ShowAsync(Show(Path.Combine(folder, "b.md")));
            Assert.Null(view.ViewportWidth);
            view.Handle(Parse("""{"type":"viewport","value":980,"generation":1}"""));
            Dispatcher.UIThread.RunJobs();

            Assert.Null(view.ViewportWidth);
            Assert.Equal(0, raised);
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_viewport_report_of_no_width_or_0_is_ignored() =>
        Headless.RunAsync(() =>
        {
            var view = new DocumentView(new RecordingAppLog(), new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);
            int raised = 0;
            view.ViewportWidthChanged += (_, _) => raised++;
            _ = view.ShowAsync(Show(Path.Combine(Path.GetTempPath(), "yamv-viewport-doc", "a.md")));

            view.Handle(Parse("""{"type":"viewport","value":0,"generation":1}"""));
            view.Handle(Parse("""{"type":"viewport","generation":1}"""));
            Dispatcher.UIThread.RunJobs();

            Assert.Null(view.ViewportWidth);
            Assert.Equal(0, raised);
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_link_from_an_older_render_is_dropped() =>
        Headless.RunAsync(() =>
        {
            var view = new DocumentView(new RecordingAppLog(), new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);
            List<LinkActivatedEventArgs> raised = [];
            view.LinkActivated += (_, link) => raised.Add(link);
            _ = view.ShowAsync(Show(Path.Combine(Path.GetTempPath(), "yamv-link-doc", "a.md")));

            view.Handle(Parse("""{"type":"link","href":"other.md","button":0,"generation":0}"""));
            view.Handle(Parse("""{"type":"link","href":"other.md","button":0}"""));
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(raised);
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_dropped_link_line_strips_line_breaks_from_its_href() =>
        Headless.RunAsync(() =>
        {
            var log = new RecordingAppLog();
            var view = new DocumentView(log, new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);
            _ = view.ShowAsync(Show(Path.Combine(Path.GetTempPath(), "yamv-link-doc", "a.md")));

            view.Handle(Parse("""{"type":"link","href":"other\r\nFORGED.md","button":0,"generation":0}"""));

            LogEntry entry = Assert.Single(log.Entries, logged => logged.Message.Contains("older render", StringComparison.Ordinal));
            Assert.DoesNotContain('\r', entry.Message);
            Assert.DoesNotContain('\n', entry.Message);
            Assert.Contains("other\uFFFD\uFFFDFORGED.md", entry.Message, StringComparison.Ordinal);
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_page_error_line_strips_line_breaks_from_the_page_message() =>
        Headless.RunAsync(() =>
        {
            var log = new RecordingAppLog();
            var view = new DocumentView(log, new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);

            view.Handle(Parse("""{"type":"error","where":"render","message":"a\nFORGED"}"""));

            LogEntry entry = Assert.Single(log.Entries);
            Assert.Equal("warning", entry.Level);
            Assert.DoesNotContain('\n', entry.Message);
            Assert.Contains("Page error in render: a\uFFFDFORGED", entry.Message, StringComparison.Ordinal);
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_file_link_with_a_host_is_raised_not_dropped() =>
        Headless.RunAsync(() =>
        {
            var view = new DocumentView(new RecordingAppLog(), new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);
            List<LinkActivatedEventArgs> raised = [];
            view.LinkActivated += (_, link) => raised.Add(link);

            view.Handle(Parse("""{"type":"link","href":"file://host/share/doc.md","button":0,"generation":0}"""));
            Dispatcher.UIThread.RunJobs();

            LinkActivatedEventArgs link = Assert.Single(raised);
            Assert.Equal("host", link.ResolvedUri?.Host);
            Assert.Equal(LinkSource.Click, link.Source);
            Assert.Null(link.DocumentPath);
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(true, "window.yamv.setCardOpen(true)")]
    [InlineData(false, "window.yamv.setCardOpen(false)")]
    public void The_card_open_script_names_the_page_function_the_page_defines(bool open, string script) =>
        Assert.Equal(script, DocumentView.CardOpenScript(open));

    [Fact]
    public Task A_page_that_loads_again_is_told_again_that_a_card_is_up() =>
        Headless.RunAsync(async () =>
        {
            var log = new RecordingAppLog();
            var view = new DocumentView(log, new Uri("file:///C:/yamv-tests/no-template.html"), elevated: false);
            view.Handle(Parse("""{"type":"ready"}"""));
            await view.SetCardOpenAsync(true);
            int Told() => log.Entries.Count(entry => entry.Message.Contains("window.yamv.setCardOpen(true)", StringComparison.Ordinal));
            int before = Told();

            view.Handle(Parse("""{"type":"ready"}"""));

            Dispatcher.UIThread.RunJobs();
            Assert.Equal(before + 1, Told());
        });

    [Fact]
    public void Render_options_reach_the_page_under_the_names_it_reads()
    {
        var options = new RenderOptions(7, @"C:\docs\a.md", 0.5, 42, 0.35, null, false, false, "file:///C:/docs/", "file:///C:/", "760px");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(options, PageJsonContext.Default.RenderOptions));
        JsonElement root = json.RootElement;

        Assert.Equal(@"C:\docs\a.md", root.GetProperty("documentPath").GetString());
        Assert.Equal(42, root.GetProperty("anchorLine").GetInt32());
        Assert.Equal(0.35, root.GetProperty("anchorOffset").GetDouble());
        Assert.Equal(0.5, root.GetProperty("scroll").GetDouble());
        Assert.Equal("760px", root.GetProperty("contentWidth").GetString());
        Assert.Equal(7, root.GetProperty("generation").GetInt32());
    }
}
