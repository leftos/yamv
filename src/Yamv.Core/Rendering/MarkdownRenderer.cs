using System.Diagnostics.CodeAnalysis;
using System.Text;
using Markdig;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Yamv.Core.Rendering;

/// <summary>Turns markdown source into the HTML the viewer shows.</summary>
public sealed class MarkdownRenderer
{
    /// <summary>
    /// Built once and shared: a built pipeline is immutable and rendering from it is thread-safe.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = BuildPipeline();

    /// <summary>Renders <paramref name="markdown"/> into HTML and reports what the viewer must prepare for.</summary>
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The viewer receives a renderer instance it can replace, so the API stays an instance method."
    )]
    public RenderedDocument Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        MarkdownDocument document = Markdown.Parse(markdown, Pipeline);
        MarkSourceLines(document);

        return new RenderedDocument(
            Markdown.ToHtml(document, Pipeline),
            FindTitle(document),
            HasDiagram(document, "mermaid"),
            document.Descendants<MathInline>().Any() || document.Descendants<MathBlock>().Any()
        );
    }

    private static MarkdownPipeline BuildPipeline()
    {
        MarkdownPipelineBuilder builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseYamlFrontMatter();

        // UseAdvancedExtensions registers auto identifiers with the default options; drop that registration
        // so the GitHub one below is the only one assigning ids.
        AutoIdentifierExtension? bundledAutoIdentifiers = builder.Extensions.OfType<AutoIdentifierExtension>().FirstOrDefault();
        if (bundledAutoIdentifiers is not null)
        {
            builder.Extensions.Remove(bundledAutoIdentifiers);
        }

        builder.UseAutoIdentifiers(AutoIdentifierOptions.GitHub);
        return builder.Build();
    }

    /// <summary>
    /// Tags every top-level block except the front matter with the zero-based source line it starts on,
    /// so the viewer can restore a scroll position. Nested blocks are found by their parent.
    /// </summary>
    private static void MarkSourceLines(MarkdownDocument document)
    {
        foreach (Block block in document)
        {
            if (block is not YamlFrontMatterBlock)
            {
                block.GetAttributes().AddPropertyIfNotExist("data-line", block.Line);
            }
        }
    }

    private static bool HasDiagram(MarkdownDocument document, string info) =>
        document.Descendants<FencedCodeBlock>().Any(block => string.Equals(block.Info?.Trim(), info, StringComparison.OrdinalIgnoreCase));

    private static string? FindTitle(MarkdownDocument document)
    {
        foreach (HeadingBlock heading in document.Descendants<HeadingBlock>())
        {
            if (heading.Level == 1)
            {
                string title = CollapseWhitespace(InlineText(heading.Inline));
                return title.Length == 0 ? null : title;
            }
        }

        return null;
    }

    private static string InlineText(ContainerInline? inlines)
    {
        var builder = new StringBuilder();
        AppendInlineText(inlines, builder);
        return builder.ToString();
    }

    /// <summary>Collapses whitespace runs into one space and trims, the way a window title reads.</summary>
    private static string CollapseWhitespace(string text) => string.Join(' ', text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Walks the heading's inline tree for its text, which is what a title needs rather than rendered HTML.</summary>
    private static void AppendInlineText(Inline? inline, StringBuilder builder)
    {
        while (inline is not null)
        {
            if (inline is ContainerInline container)
            {
                AppendInlineText(container.FirstChild, builder);
            }
            else
            {
                builder.Append(LeafText(inline));
            }

            inline = inline.NextSibling;
        }
    }

    /// <summary>The text of a leaf inline, which for several kinds is not rendered HTML text.</summary>
    private static string? LeafText(Inline inline) =>
        inline switch
        {
            LiteralInline literal => literal.Content.ToString(),
            CodeInline code => code.Content,
            HtmlEntityInline entity => entity.Transcoded.ToString(),
            AutolinkInline autolink => autolink.Url,
            MathInline math => math.Content.ToString(),
            AbbreviationInline abbreviation => abbreviation.Abbreviation.Label,
            LineBreakInline => " ",
            _ => null,
        };
}
