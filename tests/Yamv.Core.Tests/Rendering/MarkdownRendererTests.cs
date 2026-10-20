using Yamv.Core.Rendering;

namespace Yamv.Core.Tests.Rendering;

public sealed class MarkdownRendererTests
{
    private readonly MarkdownRenderer _renderer = new();

    [Fact]
    public void Render_GfmTable_RendersTable()
    {
        string html = Render(
            """
            | Name | Value |
            |---|---|
            | a | 1 |
            """
        ).Html;

        Assert.Contains("<table", html);
        Assert.Contains("<th>Name</th>", html);
    }

    [Fact]
    public void Render_TaskList_RendersDisabledCheckbox()
    {
        string html = Render("- [x] done\n- [ ] todo").Html;

        Assert.Contains("<input disabled=\"disabled\" type=\"checkbox\"", html);
    }

    [Fact]
    public void Render_StrikethroughFootnotesAndAutolinks_Render()
    {
        string html = Render(
            """
            ~~gone~~

            A note[^1].

            [^1]: The footnote.

            <https://example.com>
            """
        ).Html;

        Assert.Contains("<del>gone</del>", html);
        Assert.Contains("class=\"footnotes\"", html);
        Assert.Contains("href=\"https://example.com\"", html);
    }

    [Fact]
    public void Render_Headings_GetGitHubStyleUniqueIds()
    {
        string html = Render("## Hello World\n\n## Hello World").Html;

        Assert.Contains("id=\"hello-world\"", html);
        Assert.Contains("id=\"hello-world-1\"", html);
    }

    [Fact]
    public void Render_TopLevelBlocks_CarryTheirSourceLine()
    {
        string html = Render(
            """
            ---
            title: Test
            ---
            # Heading

            Paragraph.

            - item

            | A |
            |---|
            | 1 |

            ```c#
            var x = 1;
            ```
            """
        ).Html;

        Assert.Contains("<h1 id=\"heading\" data-line=\"3\">", html);
        Assert.Contains("<p data-line=\"5\">", html);
        Assert.Contains("<ul data-line=\"7\">", html);
        Assert.Contains("<table data-line=\"9\">", html);
        Assert.Contains("<code class=\"language-c#\" data-line=\"13\">", html);
    }

    [Fact]
    public void Render_FrontMatter_IsNotRenderedAndTitleComesFromTheHeading()
    {
        RenderedDocument document = Render(
            """
            ---
            title: Internal
            ---
            # Real Title

            Body.
            """
        );

        Assert.DoesNotContain("Internal", document.Html);
        Assert.Equal("Real Title", document.Title);
    }

    [Fact]
    public void Render_NoH1_HasNoTitle() => Assert.Null(Render("Just a paragraph.").Title);

    [Fact]
    public void Render_Title_IsThePlainTextOfTheHeading() => Assert.Equal("Hello world code", Render("# Hello *world* `code`").Title);

    [Fact]
    public void Render_MermaidFence_SetsFlagAndRendersDiagramMarkup()
    {
        RenderedDocument document = Render(
            """
            ```mermaid
            graph TD;
              A-->B;
            ```
            """
        );

        Assert.True(document.HasMermaid);
        Assert.Contains("<pre class=\"mermaid\" data-line=\"0\">graph TD;\n  A-->B;\n</pre>", document.Html);
    }

    [Fact]
    public void Render_CodeFence_IsNotADiagram()
    {
        RenderedDocument document = Render("```c#\nvar x = 1;\n```");

        Assert.False(document.HasMermaid);
        Assert.Contains("class=\"language-c#\"", document.Html);
    }

    [Fact]
    public void Render_InlineAndBlockMath_SetHasMath()
    {
        RenderedDocument inline = Render("$x^2$");
        Assert.True(inline.HasMath);
        Assert.Contains("<span class=\"math\">", inline.Html);

        RenderedDocument block = Render("$$\nx^2\n$$");
        Assert.True(block.HasMath);
        Assert.Contains("<div class=\"math\" data-line=\"0\">", block.Html);
    }

    [Fact]
    public void Render_DollarAmounts_AreNotMath() => Assert.False(Render("$5 and $6").HasMath);

    [Fact]
    public void Render_EmptyInput_GivesEmptyDocument()
    {
        RenderedDocument document = Render(string.Empty);

        Assert.Equal(string.Empty, document.Html);
        Assert.Null(document.Title);
        Assert.False(document.HasMermaid);
        Assert.False(document.HasMath);
    }

    private RenderedDocument Render(string markdown) => _renderer.Render(markdown);
}
