using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.DefinitionLists;
using Markdig.Syntax;
using Yamv.Core.Rendering;

namespace Yamv.Core.Tests.Rendering;

public sealed partial class RawSelectionTests
{
    private const string Plain = "one two three";
    private const string Bold = "see **the guide** for more";
    private const string Link = "see [the guide](https://example.com) now";
    private const string TwoParagraphs = "one two\n\nthree four";
    private const string Heading = "## Title\n\nbody";
    private const string TightList = "- a\n- b";
    private const string TightListLong = "- start rest of a\n- b";
    private const string Fence = "```\nfirst\nsecond line\n```";
    private const string Escape = "a \\* b";
    private const string Entity = "x &amp; y";
    private const string InlineMath = "a $x^2$ b c";
    private const string Cells = "| A | B |\n|---|---|\n| one two | x |";
    private const string Mermaid = "```mermaid\ngraph TD;\n  A-->B;\n```";
    private const string RawHtml = "one\n\n<div>raw</div>\n\ntwo";
    private const string Crlf = "one two\r\n\r\nthree four";
    private const string ParagraphThenHeading = "para one\n\n## Next";
    private const string ParagraphThenQuote = "para\n\n> quoted";
    private const string ParagraphThenBold = "para\n\n**Bold** rest";
    private const string Footnote = "Text[^1].\n\nMore.\n\n[^1]: The note.";
    private const string FootnoteEndingInQuote = "Hello world[^1].\n\n[^1]: Note.\n\n    > quoted";
    private const string DefinitionList = "before\n\nTerm\n: The definition\n\nafter";
    private const string Footer = "para\n\n^^ footer words here";
    private const string UnpaddedTable = "|A|B|\n|-|-|\n|x|y|";
    private const string UnpaddedTableThenParagraph = "|A|B|\n|-|-|\n|x|y|\n\nafter";
    private const string MarkEdge = "see **bold** after";
    private const string TaskItems = "- [x] done\n- [ ] todo";
    private const string CrlfSoftWrap = "foo\r\nbar baz";
    private const string Alert = "> [!NOTE]\n> Alert text";
    private const string CustomContainer = ":::spoiler\ncontent\n:::";
    private const string Figure = "^^^\n![img](x.png)\n^^^ Caption";
    private const string Abbreviation = "*[HTML]: Hyper Text\n\nUse HTML here";
    private const string LooseList = "- a\n\n- b";
    private const string GridTable = "+-----+\n| a   |\n|     |\n| - b |\n+-----+";
    private const string ParagraphThenTable = "para\n\n| A | B |\n|---|---|\n| x | y |";
    private const string ParagraphThenUnpaddedTable = "para\n\n|A|B|\n|-|-|\n|x|y|";
    private const string ParagraphThenFootnote = "Body[^1].\n\nLast para.\n\n[^1]: The note.";
    private const string NestedMarksEndingTogether = "a **b *c*** d";
    private const string NestedMarksStartingTogether = "a ***b* c** d";
    private const string ItemHoldingDefinitionList = "- Term\n  : The definition\n- b";
    private const string ParagraphThenItemHoldingDefinitionList = "intro\n\n- Term\n  : The definition";
    private const string Autolink = "see <https://x.y> after";
    private const string ItemHoldingDefinitionListThenParagraph = "intro\n\n- Term\n  : The definition\n\nafter";
    private const string ParagraphThenGridTable = "para\n\n+-----+-----+\n| a   | b   |\n+=====+=====+\n| x   | y   |\n+-----+-----+";

    /// <summary>Marks where a tagged block child opens or closes in its parent's page text.</summary>
    private const char BlockBoundary = '\u0001';

    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "p",
        "ul",
        "ol",
        "li",
        "table",
        "thead",
        "tbody",
        "tr",
        "th",
        "td",
        "pre",
        "blockquote",
        "div",
        "h1",
        "h2",
        "h3",
        "h4",
        "h5",
        "h6",
        "hr",
        "figure",
        "figcaption",
        "footer",
        "dl",
        "dt",
        "dd",
    };

    private static readonly string[] CaseStrings =
    [
        ParagraphThenHeading,
        ParagraphThenQuote,
        ParagraphThenBold,
        Footnote,
        FootnoteEndingInQuote,
        DefinitionList,
        Footer,
        UnpaddedTable,
        UnpaddedTableThenParagraph,
        MarkEdge,
        TaskItems,
        CrlfSoftWrap,
        Alert,
        CustomContainer,
        Figure,
        Abbreviation,
        LooseList,
        GridTable,
        ParagraphThenTable,
        ParagraphThenUnpaddedTable,
        ParagraphThenFootnote,
        NestedMarksEndingTogether,
        NestedMarksStartingTogether,
        ItemHoldingDefinitionList,
        ParagraphThenItemHoldingDefinitionList,
        Autolink,
        ItemHoldingDefinitionListThenParagraph,
        ParagraphThenGridTable,
        Plain,
        Bold,
        Link,
        TwoParagraphs,
        Heading,
        TightList,
        TightListLong,
        Fence,
        Escape,
        Entity,
        InlineMath,
        Cells,
        Mermaid,
        RawHtml,
        Crlf,
    ];

    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal) { "img", "br", "hr", "input", "meta", "link", "wbr" };

    [Fact]
    public void Extract_PlainTextInOneParagraph_IsThatText()
    {
        int p = Src(Plain, "p");

        Assert.Equal("two", RawSelection.Extract(Plain, new RawPoint(p, null, 4), new RawPoint(p, null, 7)));
    }

    [Fact]
    public void Extract_FromStartToTextAfterABoldMark_KeepsTheWholeMark()
    {
        int p = Src(Bold, "p");
        int strong = Src(Bold, "strong");

        Assert.Equal("see **the guide** for", RawSelection.Extract(Bold, new RawPoint(p, null, 0), new RawPoint(p, strong, 4)));
    }

    [Fact]
    public void Extract_EndInsideTheBold_WidensToTheMarksEnd()
    {
        int p = Src(Bold, "p");
        int strong = Src(Bold, "strong");

        Assert.Equal("see **the guide**", RawSelection.Extract(Bold, new RawPoint(p, null, 0), new RawPoint(strong, null, 6)));
    }

    [Fact]
    public void Extract_InsideALinksText_IsTheWholeLink()
    {
        int a = Src(Link, "a");

        Assert.Equal("[the guide](https://example.com)", RawSelection.Extract(Link, new RawPoint(a, null, 2), new RawPoint(a, null, 6)));
    }

    [Fact]
    public void Extract_AcrossTwoParagraphs_KeepsTheBlankLine()
    {
        int first = Src(TwoParagraphs, "p");
        int second = Src(TwoParagraphs, "p", 1);

        Assert.Equal("two\n\nthree", RawSelection.Extract(TwoParagraphs, new RawPoint(first, null, 4), new RawPoint(second, null, 5)));
    }

    [Fact]
    public void Extract_WholeHeading_IncludesItsMarker()
    {
        int h2 = Src(Heading, "h2");

        Assert.Equal("## Title", RawSelection.Extract(Heading, new RawPoint(h2, null, 0), new RawPoint(h2, null, 5)));
    }

    [Fact]
    public void Extract_TwoWholeItemsOfATightList_IncludesBothMarkers()
    {
        int first = Src(TightList, "li");
        int second = Src(TightList, "li", 1);

        Assert.Equal("- a\n- b", RawSelection.Extract(TightList, new RawPoint(first, null, 0), new RawPoint(second, null, 1)));
    }

    [Fact]
    public void Extract_SecondHalfOfItemOneAndAllOfItemTwo_StartsInsideItemOne()
    {
        int first = Src(TightListLong, "li");
        int second = Src(TightListLong, "li", 1);

        Assert.Equal("rest of a\n- b", RawSelection.Extract(TightListLong, new RawPoint(first, null, 6), new RawPoint(second, null, 1)));
    }

    [Fact]
    public void Extract_PartOfACodeBlocksSecondLine_IsThatPartOnly()
    {
        int code = Src(Fence, "code");

        Assert.Equal("seco", RawSelection.Extract(Fence, new RawPoint(code, null, 6), new RawPoint(code, null, 10)));
    }

    [Fact]
    public void Extract_WholeCodeBlock_IncludesTheFences()
    {
        int code = Src(Fence, "code");

        Assert.Equal(Fence, RawSelection.Extract(Fence, new RawPoint(code, null, 0), new RawPoint(code, null, 18)));
    }

    [Fact]
    public void Extract_EndInsideAnEscape_SnapsToAfterTheEscape()
    {
        int p = Src(Escape, "p");

        Assert.Equal("a \\*", RawSelection.Extract(Escape, new RawPoint(p, null, 0), new RawPoint(p, null, 3)));
    }

    [Fact]
    public void Extract_PointAfterAnEntity_CountsTheEntityAsOneCharacter()
    {
        int p = Src(Entity, "p");

        Assert.Equal("y", RawSelection.Extract(Entity, new RawPoint(p, null, 4), new RawPoint(p, null, 5)));
    }

    [Fact]
    public void Extract_PointAfterInlineMath_CountsFromTheMathsEnd()
    {
        int p = Src(InlineMath, "p");
        int math = Src(InlineMath, "span");

        Assert.Equal("b", RawSelection.Extract(InlineMath, new RawPoint(p, math, 1), new RawPoint(p, math, 2)));
    }

    [Fact]
    public void Extract_PartOfATableCell_IsThatPartOnly()
    {
        int td = Src(Cells, "td");

        Assert.Equal("two", RawSelection.Extract(Cells, new RawPoint(td, null, 4), new RawPoint(td, null, 7)));
    }

    [Fact]
    public void Extract_PartOfAMermaidBlock_IsTheWholeFencedBlock()
    {
        int pre = Src(Mermaid, "pre");

        Assert.Equal(Mermaid, RawSelection.Extract(Mermaid, new RawPoint(pre, null, 2), new RawPoint(pre, null, 5)));
    }

    [Fact]
    public void Extract_StartSnappedToTheParagraphBeforeRawHtml_StartsAfterThatParagraph()
    {
        int first = Src(RawHtml, "p");
        int second = Src(RawHtml, "p", 1);

        Assert.Equal("<div>raw</div>\n\ntwo", RawSelection.Extract(RawHtml, new RawPoint(first, null, -1), new RawPoint(second, null, 3)));
    }

    [Fact]
    public void Extract_CrlfDocumentAcrossTwoParagraphs_KeepsCrlf()
    {
        int first = Src(Crlf, "p");
        int second = Src(Crlf, "p", 1);

        Assert.Equal("two\r\n\r\nthree", RawSelection.Extract(Crlf, new RawPoint(first, null, 4), new RawPoint(second, null, 5)));
    }

    [Fact]
    public void Extract_UnknownDataSrc_IsNull()
    {
        int p = Src(Plain, "p");

        Assert.Null(RawSelection.Extract(Plain, new RawPoint(999, null, 0), new RawPoint(p, null, 3)));
    }

    [Fact]
    public void Extract_AfterThatIsNotATaggedChildOfNode_IsNull()
    {
        int first = Src(TwoParagraphs, "p");
        int second = Src(TwoParagraphs, "p", 1);

        Assert.Null(RawSelection.Extract(TwoParagraphs, new RawPoint(first, second, 0), new RawPoint(second, null, 3)));
    }

    [Fact]
    public void Extract_StartAfterEnd_IsNull()
    {
        int p = Src(Plain, "p");

        Assert.Null(RawSelection.Extract(Plain, new RawPoint(p, null, 7), new RawPoint(p, null, 4)));
    }

    [Fact]
    public void Extract_OffsetOfMinusTwo_IsNull()
    {
        int p = Src(Plain, "p");

        Assert.Null(RawSelection.Extract(Plain, new RawPoint(p, null, -2), new RawPoint(p, null, 3)));
    }

    [Fact]
    public void Extract_TripleClickEndAtTheNextHeadingsStart_StopsBeforeItsMarker()
    {
        int p = Src(ParagraphThenHeading, "p");
        int h2 = Src(ParagraphThenHeading, "h2");

        Assert.Equal("para one", RawSelection.Extract(ParagraphThenHeading, new RawPoint(p, null, 0), new RawPoint(h2, null, 0)));
    }

    [Fact]
    public void Extract_TripleClickEndAtTheNextItemsStart_StopsBeforeItsMarker()
    {
        int first = Src(TightList, "li");
        int second = Src(TightList, "li", 1);

        Assert.Equal("- a", RawSelection.Extract(TightList, new RawPoint(first, null, 0), new RawPoint(second, null, 0)));
    }

    [Fact]
    public void Extract_TripleClickEndAtAQuotesStart_StopsBeforeItsMarker()
    {
        int p = Src(ParagraphThenQuote, "p");
        int quoted = Src(ParagraphThenQuote, "p", 1);

        Assert.Equal("para", RawSelection.Extract(ParagraphThenQuote, new RawPoint(p, null, 0), new RawPoint(quoted, null, 0)));
    }

    [Fact]
    public void Extract_TripleClickEndAtALeadingBold_StopsBeforeTheBold()
    {
        int p = Src(ParagraphThenBold, "p");
        int next = Src(ParagraphThenBold, "p", 1);
        int strong = Src(ParagraphThenBold, "strong");

        Assert.Equal("para", RawSelection.Extract(ParagraphThenBold, new RawPoint(p, null, 0), new RawPoint(next, null, 0)));
        Assert.Equal("para", RawSelection.Extract(ParagraphThenBold, new RawPoint(p, null, 0), new RawPoint(strong, null, 0)));
    }

    [Fact]
    public void Extract_OnlyLineBreaksSelected_IsNull()
    {
        int p = Src(ParagraphThenHeading, "p");
        int h2 = Src(ParagraphThenHeading, "h2");

        Assert.Null(RawSelection.Extract(ParagraphThenHeading, new RawPoint(p, null, -1), new RawPoint(h2, null, 0)));
    }

    [Fact]
    public void Extract_WholeFootnoteParagraph_IsItsTextWithoutTheBackLink()
    {
        int note = Src(Footnote, "p", 2);

        Assert.Equal("The note.", RawSelection.Extract(Footnote, new RawPoint(note, null, 0), new RawPoint(note, null, 10)));
    }

    [Fact]
    public void Extract_PartOfAFootnotesLastParagraph_IsThatPartOnly()
    {
        int note = Src(Footnote, "p", 2);

        Assert.Equal("The", RawSelection.Extract(Footnote, new RawPoint(note, null, 0), new RawPoint(note, null, 3)));
    }

    [Fact]
    public void Extract_FootnoteEndingInAQuote_DoesNotTagItsBackLinkParagraph()
    {
        int p = Src(FootnoteEndingInQuote, "p");

        Assert.Equal("world", RawSelection.Extract(FootnoteEndingInQuote, new RawPoint(p, null, 6), new RawPoint(p, null, 11)));
    }

    [Fact]
    public void Extract_SnapsAroundAnUntaggedDefinitionList_IsTheWholeList()
    {
        string html = new MarkdownRenderer().Render(DefinitionList).Html;
        int before = Src(DefinitionList, "p");
        int after = Src(DefinitionList, "p", 1);

        Assert.DoesNotMatch("<d[ltd]\\b[^>]*data-src", html);
        Assert.Equal("Term\n: The definition", RawSelection.Extract(DefinitionList, new RawPoint(before, null, -1), new RawPoint(after, null, -1)));
    }

    [Fact]
    public void Extract_PartOfAFooter_IsThatPartOnly()
    {
        int footer = Src(Footer, "footer");

        Assert.Equal("words", RawSelection.Extract(Footer, new RawPoint(footer, null, 7), new RawPoint(footer, null, 12)));
    }

    [Fact]
    public void Extract_WholeTextOfAnUnpaddedCell_DoesNotWidenToTheRow()
    {
        int td = Src(UnpaddedTable, "td", 1);

        Assert.Equal("y", RawSelection.Extract(UnpaddedTable, new RawPoint(td, null, 0), new RawPoint(td, null, 1)));
    }

    [Fact]
    public void Extract_FromAnUnpaddedCellIntoAParagraph_StartsAtTheCellsText()
    {
        int td = Src(UnpaddedTableThenParagraph, "td", 1);
        int p = Src(UnpaddedTableThenParagraph, "p");

        Assert.Equal("y|\n\nafter", RawSelection.Extract(UnpaddedTableThenParagraph, new RawPoint(td, null, 0), new RawPoint(p, null, 5)));
    }

    [Fact]
    public void Extract_StartAtTheEndOfAMarksText_StartsAfterTheMark()
    {
        int p = Src(MarkEdge, "p");
        int strong = Src(MarkEdge, "strong");

        Assert.Equal(" after", RawSelection.Extract(MarkEdge, new RawPoint(strong, null, 4), new RawPoint(p, strong, 6)));
    }

    [Fact]
    public void Extract_TaskItemsFromTheFirstItemsText_IncludesBothMarkersAndCheckboxes()
    {
        int first = Src(TaskItems, "li");
        int second = Src(TaskItems, "li", 1);
        int firstBox = Src(TaskItems, "input");
        int secondBox = Src(TaskItems, "input", 1);

        Assert.Equal("- [x] done\n- [ ] todo", RawSelection.Extract(TaskItems, new RawPoint(first, firstBox, 1), new RawPoint(second, secondBox, 5)));
    }

    [Fact]
    public void Extract_StartAfterACrlfSoftWrap_DropsTheLineBreak()
    {
        int p = Src(CrlfSoftWrap, "p");

        Assert.Equal("bar", RawSelection.Extract(CrlfSoftWrap, new RawPoint(p, null, 4), new RawPoint(p, null, 7)));
    }

    [Fact]
    public void Extract_EndAtAHeaderCellsStart_StopsBeforeTheTable()
    {
        int p = Src(ParagraphThenTable, "p");
        int th = Src(ParagraphThenTable, "th");
        int unpaddedP = Src(ParagraphThenUnpaddedTable, "p");
        int unpaddedTh = Src(ParagraphThenUnpaddedTable, "th");

        Assert.Equal("para", RawSelection.Extract(ParagraphThenTable, new RawPoint(p, null, 0), new RawPoint(th, null, 0)));
        Assert.Equal("para", RawSelection.Extract(ParagraphThenUnpaddedTable, new RawPoint(unpaddedP, null, 0), new RawPoint(unpaddedTh, null, 0)));
    }

    [Fact]
    public void Extract_EndAtTheNextCellsStart_StopsBeforeItsPipe()
    {
        int x = Src(UnpaddedTable, "td");
        int y = Src(UnpaddedTable, "td", 1);

        Assert.Equal("x", RawSelection.Extract(UnpaddedTable, new RawPoint(x, null, 0), new RawPoint(y, null, 0)));
    }

    [Fact]
    public void Extract_EndAtAFootnotesStart_StopsBeforeItsLabel()
    {
        int last = Src(ParagraphThenFootnote, "p", 1);
        int note = Src(ParagraphThenFootnote, "p", 2);

        Assert.Equal("Last para.", RawSelection.Extract(ParagraphThenFootnote, new RawPoint(last, null, 0), new RawPoint(note, null, 0)));
    }

    [Fact]
    public void Extract_StartAtTheEndOfNestedMarksEndingTogether_StartsAfterBoth()
    {
        int p = Src(NestedMarksEndingTogether, "p");
        int em = Src(NestedMarksEndingTogether, "em");
        int strong = Src(NestedMarksEndingTogether, "strong");

        Assert.Equal(" d", RawSelection.Extract(NestedMarksEndingTogether, new RawPoint(em, null, 1), new RawPoint(p, strong, 2)));
    }

    [Fact]
    public void Extract_EndAtTheStartOfNestedMarksStartingTogether_EndsBeforeBoth()
    {
        int p = Src(NestedMarksStartingTogether, "p");
        int em = Src(NestedMarksStartingTogether, "em");

        Assert.Equal("a", RawSelection.Extract(NestedMarksStartingTogether, new RawPoint(p, null, 0), new RawPoint(em, null, 0)));
    }

    [Fact]
    public void ModeledText_ItemHoldingADefinitionList_StopsAtTheList()
    {
        MarkdownDocument document = Markdown.Parse(ItemHoldingDefinitionList, MarkdownRenderer.Pipeline);
        ListItemBlock item = document.Descendants<ListItemBlock>().First();

        Assert.Equal(string.Empty, RawSelection.ModeledText(item));
    }

    [Fact]
    public void Extract_EndAfterADefinitionListInAnItem_DoesNotCountTheListsText()
    {
        int p = Src(ParagraphThenItemHoldingDefinitionList, "p");
        int item = Src(ParagraphThenItemHoldingDefinitionList, "li");
        Assert.Equal(
            "intro\n\n- Term\n  : The definition",
            RawSelection.Extract(ParagraphThenItemHoldingDefinitionList, new RawPoint(p, null, 0), new RawPoint(item, null, 40))
        );
    }

    [Fact]
    public void Extract_EndAtTheStartOfAnItemOpeningWithADefinitionList_EndsBeforeTheItem()
    {
        int p = Src(ItemHoldingDefinitionListThenParagraph, "p");
        int item = Src(ItemHoldingDefinitionListThenParagraph, "li");

        Assert.Equal("intro", RawSelection.Extract(ItemHoldingDefinitionListThenParagraph, new RawPoint(p, null, 0), new RawPoint(item, null, 0)));
    }

    [Fact]
    public void Extract_StartAtTheStartOfAnItemOpeningWithADefinitionList_IncludesItsMarker()
    {
        int item = Src(ItemHoldingDefinitionListThenParagraph, "li");
        int after = Src(ItemHoldingDefinitionListThenParagraph, "p", 1);

        Assert.Equal(
            "- Term\n  : The definition\n\nafter",
            RawSelection.Extract(ItemHoldingDefinitionListThenParagraph, new RawPoint(item, null, 0), new RawPoint(after, null, 5))
        );
    }

    [Fact]
    public void Extract_EndAtAGridTablesFirstCell_StopsBeforeTheTopBorder()
    {
        int p = Src(ParagraphThenGridTable, "p");
        int th = Src(ParagraphThenGridTable, "th");

        Assert.Equal("para", RawSelection.Extract(ParagraphThenGridTable, new RawPoint(p, null, 0), new RawPoint(th, null, 0)));
    }

    [Fact]
    public void Extract_StartAtTheEndOfAnAutolinksText_StartsAfterTheAutolink()
    {
        int p = Src(Autolink, "p");
        int a = Src(Autolink, "a");

        Assert.Equal(" after", RawSelection.Extract(Autolink, new RawPoint(a, null, 11), new RawPoint(p, a, 6)));
    }

    /// <summary>
    /// The definition list as first tagged: the HTML that shape produced, read by the same parser as the agreement test,
    /// does not agree with the current model. Against that shape's model (the term inside the item), parent offsets
    /// alone agree, since all three share one <c>data-src</c>; the depth tells them apart.
    /// </summary>
    [Fact]
    public void Nesting_SharedDataSrcAtDifferentDepths_DoesNotAgree()
    {
        const string html = "<dl data-src=\"0\"><dt data-src=\"0\">Term</dt><dd data-src=\"0\">The definition</dd></dl>";
        List<(int Src, int Parent, int Depth)> page = Nesting(HtmlTexts(html).Select(entry => (entry.Src, entry.Parent, entry.Depth)));
        MarkdownDocument document = Markdown.Parse("Term\n: The definition", MarkdownRenderer.Pipeline);
        List<(int Src, int Parent, int Depth)> model = Nesting(
            document.Descendants().Where(RawSelection.IsTaggedInHtml).Select(node => (node.Span.Start, ParentSrc(node), TaggedDepth(node)))
        );
        (int, int, int)[] firstModel = [(0, -1, 0), (0, 0, 1), (0, 0, 2)];

        Assert.Equal([(0, -1, 0), (0, 0, 1), (0, 0, 1)], page);
        Assert.NotEqual(page, model);
        Assert.NotEqual(page, Nesting(firstModel));
        Assert.Equal(page.Select(entry => (entry.Src, entry.Parent)), Nesting(firstModel).Select(entry => (entry.Src, entry.Parent)));
    }

    /// <summary>
    /// Pins the model of the page to Markdig's real output: the same elements are tagged, each nested under the same
    /// tagged parent at the same tagged depth (the depth is what catches several elements sharing one <c>data-src</c>
    /// at different depths), and each text host's untagged text is what the mapping walks. The newlines Markdig writes
    /// next to a tagged block inside a container hold no source, so they are dropped from the page text; a container
    /// holding a definition list is skipped, since its walk stops at the list by design.
    /// </summary>
    [Fact]
    public void ModeledText_AgreesWithTheRenderedHtml()
    {
        foreach (string markdown in CaseStrings.Append(ReadSample()))
        {
            List<PageElement> html = HtmlTexts(new MarkdownRenderer().Render(markdown).Html);
            MarkdownDocument document = Markdown.Parse(markdown, MarkdownRenderer.Pipeline);
            List<MarkdownObject> tagged = [.. document.Descendants().Where(RawSelection.IsTaggedInHtml)];

            Assert.Equal(
                Nesting(html.Select(entry => (entry.Src, entry.Parent, entry.Depth))),
                Nesting(tagged.Select(node => (node.Span.Start, ParentSrc(node), TaggedDepth(node))))
            );
            int compared = 0;
            foreach (MarkdownObject node in tagged.Where(node => !node.Descendants<DefinitionList>().Any()))
            {
                if (RawSelection.ModeledText(node) is { } modeled)
                {
                    Assert.Contains(html, entry => entry.Src == node.Span.Start && Comparable(entry.Text) == modeled);
                    compared++;
                }
            }

            Assert.True(compared > 0, $"No text was compared for: {markdown}");
        }
    }

    private static List<(int Src, int Parent, int Depth)> Nesting(IEnumerable<(int Src, int Parent, int Depth)> entries) => [.. entries.Order()];

    private static int ParentSrc(MarkdownObject node) => RawSelection.TaggedParent(node)?.Span.Start ?? -1;

    private static int TaggedDepth(MarkdownObject node)
    {
        int depth = 0;
        for (MarkdownObject? parent = RawSelection.TaggedParent(node); parent is not null; parent = RawSelection.TaggedParent(parent))
        {
            depth++;
        }

        return depth;
    }

    /// <summary>Drops the newlines next to a tagged block's boundary (marked while parsing) and the marks themselves.</summary>
    private static string Comparable(string text) => BlockBoundaryPattern().Replace(text, string.Empty);

    /// <summary>The <c>data-src</c> of the <paramref name="occurrence"/>-th <paramref name="tag"/> element in the rendered HTML.</summary>
    private static int Src(string markdown, string tag, int occurrence = 0)
    {
        string html = new MarkdownRenderer().Render(markdown).Html;
        MatchCollection matches = Regex.Matches(html, $"<{tag}\\b[^>]*\\bdata-src=\"(\\d+)\"");
        return int.Parse(matches[occurrence].Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The text of each tagged element as the page sees it outside tagged elements below it: tagged children are dropped
    /// whole, other tags and comments are stripped, and entities are decoded. Each comes with its nearest tagged
    /// ancestor's <c>data-src</c> (-1 for none) and its count of tagged ancestors; where a tagged block child opens or
    /// closes, the parent's text gets a <see cref="BlockBoundary"/> mark.
    /// </summary>
    private static List<PageElement> HtmlTexts(string html)
    {
        html = CommentPattern().Replace(html, string.Empty);
        var elements = new List<(int Src, int Parent, int Depth)>();
        var texts = new List<StringBuilder>();
        var open = new List<(string Name, int Entry, bool Block)>();
        int position = 0;
        foreach (Match tag in TagPattern().Matches(html))
        {
            AppendText(html[position..tag.Index]);
            position = tag.Index + tag.Length;
            string name = tag.Groups["name"].Value.ToLowerInvariant();
            if (tag.Groups["close"].Success)
            {
                Close(name);
                continue;
            }

            Match src = SrcPattern().Match(tag.Groups["attributes"].Value);
            int entry = -1;
            bool block = IsBlock(name, open);
            if (src.Success)
            {
                int parent = open.FindLastIndex(element => element.Entry >= 0);
                elements.Add(
                    (
                        int.Parse(src.Groups[1].Value, CultureInfo.InvariantCulture),
                        parent >= 0 ? elements[open[parent].Entry].Src : -1,
                        open.Count(element => element.Entry >= 0)
                    )
                );
                MarkBoundary(block);
                texts.Add(new StringBuilder());
                entry = texts.Count - 1;
            }

            if (!VoidElements.Contains(name) && !tag.Groups["attributes"].Value.EndsWith('/'))
            {
                open.Add((name, entry, block));
            }
        }

        AppendText(html[position..]);
        return [.. elements.Select((element, index) => new PageElement(element.Src, element.Parent, element.Depth, texts[index].ToString()))];

        void Close(string name)
        {
            int index = open.FindLastIndex(element => element.Name == name);
            if (index < 0)
            {
                return;
            }

            bool taggedBlock = open[index].Entry >= 0 && open[index].Block;
            open.RemoveRange(index, open.Count - index);
            MarkBoundary(taggedBlock);
        }

        void MarkBoundary(bool taggedBlock)
        {
            int index = open.FindLastIndex(element => element.Entry >= 0);
            if (taggedBlock && index >= 0)
            {
                texts[open[index].Entry].Append(BlockBoundary);
            }
        }

        void AppendText(string text)
        {
            int index = open.FindLastIndex(element => element.Entry >= 0);
            if (index >= 0 && text.Length > 0)
            {
                texts[open[index].Entry].Append(WebUtility.HtmlDecode(text));
            }
        }
    }

    /// <summary>Whether an element is a block in the page: a block tag, or the <c>code</c> a code block writes inside <c>pre</c>.</summary>
    private static bool IsBlock(string name, List<(string Name, int Entry, bool Block)> open) =>
        BlockElements.Contains(name) || (name == "code" && open.Count > 0 && open[^1].Name == "pre");

    private static string ReadSample()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Yamv.slnx")))
            {
                return File.ReadAllText(Path.Combine(directory.FullName, "docs", "design", "fixtures", "sample.md"));
            }
        }

        throw new InvalidOperationException($"No folder above {AppContext.BaseDirectory} holds Yamv.slnx.");
    }

    [GeneratedRegex("<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9-]*)(?<attributes>[^>]*)>")]
    private static partial Regex TagPattern();

    /// <summary>A tagged element of the page: its <c>data-src</c>, its tagged parent's, its tagged depth and its own text.</summary>
    private readonly record struct PageElement(int Src, int Parent, int Depth, string Text);

    [GeneratedRegex("\n*\u0001\n*")]
    private static partial Regex BlockBoundaryPattern();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex("\\bdata-src=\"(\\d+)\"")]
    private static partial Regex SrcPattern();
}
