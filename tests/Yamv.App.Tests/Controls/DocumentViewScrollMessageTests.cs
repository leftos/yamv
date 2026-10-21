using System.Text.Json;
using Yamv.App.Controls;
using Yamv.App.Tests.Fakes;
using Yamv.Core.Session;

namespace Yamv.App.Tests.Controls;

public sealed class DocumentViewScrollMessageTests
{
    private static PageMessage Parse(string body) =>
        JsonSerializer.Deserialize(body, PageJsonContext.Default.PageMessage) ?? throw new InvalidOperationException("no message");

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
            var view = new DocumentView(new RecordingAppLog(), new Uri("file:///C:/yamv-tests/no-template.html"));
            List<ScrollPosition> raised = [];
            view.ScrollChanged += (_, position) => raised.Add(position);

            view.Handle(Parse("""{"type":"scroll","value":0.25,"line":1.5,"offset":0.4,"generation":0}"""));
            view.Handle(Parse("""{"type":"scroll","value":0.5,"line":8,"offset":0.4,"generation":0}"""));

            Assert.Equal([new ScrollPosition(0.25, null), new ScrollPosition(0.5, new ScrollAnchor(8, 0.4))], raised);
            return Task.CompletedTask;
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
