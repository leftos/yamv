using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Figures;
using Markdig.Extensions.Footers;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Helpers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Yamv.Core.Rendering;

/// <summary>One end of a selection in the rendered page, located by the <c>data-yamv-src</c> source offsets the HTML carries.</summary>
/// <param name="Node">The <c>data-yamv-src</c> of the innermost tagged element holding the end, or null when none holds it.</param>
/// <param name="After">
/// The <c>data-yamv-src</c> of the last tagged element directly inside <paramref name="Node"/>'s element (not inside another
/// tagged element below it) that ends before the end, or null when there is none.
/// </param>
/// <param name="Offset">
/// The text characters (UTF-16 code units) from the end of <paramref name="After"/>'s element, or from the start of
/// <paramref name="Node"/>'s element, to the end, counting only text outside tagged elements below <paramref name="Node"/>;
/// -1 means the end of <paramref name="Node"/>'s element for a start and its start for an end. An element with no text
/// and no tagged parent (a rule between blocks) is named by its own tag: offset 0 is just before it and offset 1 just
/// after it, for a start and an end alike.
/// </param>
/// <remarks>
/// A point with no <paramref name="Node"/>, no <paramref name="After"/> and offset 0 is the document's edge: its start
/// for a start point and its end for an end point, so raw HTML at the very top or bottom of a document can be copied.
/// Any other point without a <paramref name="Node"/> maps to nothing.
/// </remarks>
public sealed record RawPoint(int? Node, int? After, int Offset);

/// <summary>Maps a selection in the rendered page back to the markdown source it was rendered from.</summary>
public static class RawSelection
{
    /// <summary>The fence infos the code block renderer writes as diagrams or other mapped blocks instead of code.</summary>
    private static readonly FrozenSet<string> MappedInfos = ReadMappedInfos();

    /// <summary>Returns the markdown source of the selection from <paramref name="start"/> to <paramref name="end"/>.</summary>
    /// <param name="markdown">The markdown the page was rendered from.</param>
    /// <param name="start">The selection's start, as the page describes it.</param>
    /// <param name="end">The selection's end, as the page describes it.</param>
    /// <returns>
    /// The selected source, widened to whole marks and block markers, with leading line breaks and trailing whitespace
    /// trimmed; null when a point names no tagged node, the start maps after the end, or nothing but whitespace is left.
    /// </returns>
    public static string? Extract(string markdown, RawPoint start, RawPoint end)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        MarkdownDocument document = Markdown.Parse(markdown, MarkdownRenderer.Pipeline);
        Dictionary<int, List<MarkdownObject>> tagged = IndexTagged(document);
        int? from = MapPoint(markdown, tagged, start, isStart: true);
        int? to = MapPoint(markdown, tagged, end, isStart: false);
        if (from is null || to is null || from > to)
        {
            return null;
        }

        (int first, int last) = WidenToMarks(document, from.Value, to.Value);
        List<LeafBlock> leaves = [.. document.Descendants<LeafBlock>().Where(IsWideningLeaf)];
        (first, last) = (WidenStartToBlock(leaves, markdown, first, last), WidenEndToBlock(leaves, markdown, last, first));
        string copied = markdown[first..Math.Max(first, last)].TrimEnd().TrimStart('\r', '\n');
        return copied.Length == 0 ? null : copied;
    }

    /// <summary>
    /// The text the page shows directly in <paramref name="node"/>'s element, outside tagged elements below it, as the
    /// mapping counts it: a code block's lines (a mapped fence such as a diagram included, though points in one take the
    /// whole fence), or a text host's untagged inline text up to any definition list in it. Null for every other node.
    /// </summary>
    internal static string? ModeledText(MarkdownObject node)
    {
        if (node is CodeBlock code and not (MathBlock or YamlFrontMatterBlock))
        {
            return CodeText(code);
        }

        if (!IsTaggedInHtml(node) || !HostsText(node))
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (Item item in Items(node))
        {
            builder.Append(item.Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether the HTML renderer writes attributes for <paramref name="node"/>, so its element carries <c>data-yamv-src</c>.
    /// The renderer tags with it and the selection mapping reads with it, so both agree on what the page sees. A node
    /// without a source position (a footnote's back link and the paragraph made to hold it) and anything in a definition
    /// list (whose renderer writes one item's attributes on several elements) stay untagged.
    /// </summary>
    internal static bool IsTaggedInHtml(MarkdownObject node) =>
        !node.Span.IsEmpty
        && !IsInsideDefinitionList(node)
        && node switch
        {
            YamlFrontMatterBlock => false,
            ParagraphBlock paragraph => !RendersImplicitly(paragraph),
            Inline inline => IsTaggedInlineType(inline) && !IsInsideImage(inline),
            _ => IsTaggedBlockType(node),
        };

    /// <summary>The nearest ancestor whose element carries <c>data-yamv-src</c>.</summary>
    internal static MarkdownObject? TaggedParent(MarkdownObject node)
    {
        MarkdownObject? current = Parent(node);
        while (current is not null && !IsTaggedInHtml(current))
        {
            current = Parent(current);
        }

        return current;
    }

    /// <summary>The tagged nodes by source start; nodes sharing a start are nested, and the deepest comes first.</summary>
    private static Dictionary<int, List<MarkdownObject>> IndexTagged(MarkdownDocument document)
    {
        var index = new Dictionary<int, List<MarkdownObject>>();
        foreach (MarkdownObject node in document.Descendants().Where(IsTaggedInHtml))
        {
            if (!index.TryGetValue(node.Span.Start, out List<MarkdownObject>? nodes))
            {
                nodes = [];
                index[node.Span.Start] = nodes;
            }

            nodes.Add(node);
        }

        foreach (List<MarkdownObject> nodes in index.Values)
        {
            nodes.Sort((left, right) => Depth(right).CompareTo(Depth(left)));
        }

        return index;
    }

    /// <summary>Finds the point's node and its <c>After</c> child; null when either names no tagged node or they are not parent and child.</summary>
    private static (MarkdownObject Node, MarkdownObject? After)? Resolve(Dictionary<int, List<MarkdownObject>> tagged, RawPoint point)
    {
        if (point.Node is not { } source || !tagged.TryGetValue(source, out List<MarkdownObject>? nodes))
        {
            return null;
        }

        if (point.After is not { } afterSource)
        {
            return (nodes[0], null);
        }

        if (!tagged.TryGetValue(afterSource, out List<MarkdownObject>? afters))
        {
            return null;
        }

        foreach (MarkdownObject node in nodes)
        {
            MarkdownObject? after = afters.FirstOrDefault(candidate => ReferenceEquals(TaggedParent(candidate), node));
            if (after is not null)
            {
                return (node, after);
            }
        }

        return null;
    }

    private static int? MapPoint(string markdown, Dictionary<int, List<MarkdownObject>> tagged, RawPoint point, bool isStart)
    {
        if (point is { Node: null, After: null, Offset: 0 })
        {
            return isStart ? 0 : markdown.Length;
        }

        if (point.Offset < -1 || Resolve(tagged, point) is not ({ } node, var after))
        {
            return null;
        }

        if (point.Offset == -1)
        {
            return isStart ? node.Span.End + 1 : node.Span.Start;
        }

        return MapInto(markdown, node, after, point.Offset, isStart);
    }

    private static int? MapInto(string markdown, MarkdownObject node, MarkdownObject? after, int offset, bool isStart)
    {
        if (node is CodeBlock code && IsPlainCode(code))
        {
            return after is null ? CodeOffset(code, offset) : null;
        }

        if (BeforeOpeningDefinitionList(markdown, node, after, offset, isStart) is { } opening)
        {
            return opening;
        }

        return HostsText(node) ? TextOffset(markdown, node, after, offset, isStart) : WholeNode(node, after, offset, isStart);
    }

    /// <summary>
    /// A point at the very start of a node whose content opens with a definition list sits before any text the walk can
    /// count, so it takes the node's own start (its line start for a start, which keeps a list marker); null otherwise.
    /// </summary>
    private static int? BeforeOpeningDefinitionList(string markdown, MarkdownObject node, MarkdownObject? after, int offset, bool isStart)
    {
        if (after is not null || offset != 0 || Items(node) is not [{ Node: DefinitionList }, ..])
        {
            return null;
        }

        return isStart ? LineStart(markdown, node.Span.Start) : node.Span.Start;
    }

    /// <summary>
    /// A point in a node copied whole: a start at the end of the node's text (offset 1 or more of a rule, which has none)
    /// maps after it, an end at its very start maps before it, and any other point takes the whole node.
    /// </summary>
    private static int WholeNode(MarkdownObject node, MarkdownObject? after, int offset, bool isStart)
    {
        if (isStart)
        {
            bool atEnd = IsAtEnd(node, after, offset) || (node is ThematicBreakBlock && offset > 0);
            return atEnd ? node.Span.End + 1 : node.Span.Start;
        }

        return offset == 0 && after is null ? node.Span.Start : node.Span.End + 1;
    }

    /// <summary>
    /// Whether the point is past all of the node's text: only text follows <paramref name="after"/>, and no more of it
    /// than the offset.
    /// </summary>
    private static bool IsAtEnd(MarkdownObject node, MarkdownObject? after, int offset)
    {
        if (offset == 0 && after is null)
        {
            return false;
        }

        List<Item> items = Items(node);
        List<Item> rest = items[(after is null ? 0 : items.FindIndex(item => ReferenceEquals(item.Node, after)) + 1)..];
        return items.Count > 0 && rest.TrueForAll(item => item.Text is not null) && offset >= rest.Sum(item => item.Text!.Length);
    }

    /// <summary>Whether points in the node walk its text: a leaf block with inlines, or a block holding an implicit paragraph.</summary>
    private static bool HostsText(MarkdownObject node) =>
        node is LeafBlock { Inline: not null } || (node is ContainerBlock container && HostsImplicitParagraph(container));

    private static bool HostsImplicitParagraph(ContainerBlock container)
    {
        foreach (Block child in container)
        {
            bool found = child switch
            {
                ParagraphBlock paragraph => !IsTaggedInHtml(paragraph),
                ContainerBlock inner => !IsTaggedInHtml(inner) && HostsImplicitParagraph(inner),
                _ => false,
            };
            if (found)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks the untagged text after <paramref name="after"/> (or from the start) for <paramref name="offset"/> characters.
    /// An inline without a source position (a footnote's back link) counts its text but takes no width.
    /// </summary>
    private static int? TextOffset(string markdown, MarkdownObject node, MarkdownObject? after, int offset, bool isStart)
    {
        if (WalkStart(node, after) is not ({ } items, var index, var position))
        {
            return null;
        }

        int remaining = offset;
        for (; index < items.Count && items[index].Text is { } text; index++)
        {
            var inline = (Inline)items[index].Node;
            if (remaining <= text.Length)
            {
                return inline.Span.IsEmpty ? position : Land(markdown, inline, text, remaining, isStart);
            }

            remaining -= text.Length;
            position = inline.Span.IsEmpty ? position : inline.Span.End + 1;
        }

        return StopPosition(items, index, position, isStart);
    }

    /// <summary>
    /// Where a walk that ran out of text lands: at the end of the text it walked, or, when it stopped at a definition
    /// list whose text it cannot count, around the whole list (before it for a start, after it for an end).
    /// </summary>
    private static int StopPosition(List<Item> items, int index, int position, bool isStart)
    {
        if (index >= items.Count || items[index].Node is not DefinitionList list)
        {
            return position;
        }

        return isStart ? list.Span.Start : list.Span.End + 1;
    }

    /// <summary>
    /// The node's items, the index the walk starts at and the source offset it starts from; null when
    /// <paramref name="after"/> is not among them.
    /// </summary>
    private static (List<Item> Items, int Index, int Position)? WalkStart(MarkdownObject node, MarkdownObject? after)
    {
        List<Item> items = Items(node);
        if (after is null)
        {
            int first = items.FindIndex(item => !item.Node.Span.IsEmpty);
            return (items, 0, first < 0 ? node.Span.Start : items[first].Node.Span.Start);
        }

        int index = items.FindIndex(item => ReferenceEquals(item.Node, after));
        return index < 0 ? null : (items, index + 1, after.Span.End + 1);
    }

    /// <summary>
    /// The source offset <paramref name="chars"/> characters into an inline's page text. A literal maps exactly; any
    /// other inline (an entity, a line break) is atomic, so a point inside it snaps outward.
    /// </summary>
    private static int Land(string markdown, Inline inline, string text, int chars, bool isStart)
    {
        if (inline is LiteralInline literal && LiteralOffset(markdown, literal, text, chars) is { } exact)
        {
            return exact;
        }

        if (chars == 0)
        {
            return inline.Span.Start;
        }

        return chars == text.Length || !isStart ? inline.Span.End + 1 : inline.Span.Start;
    }

    /// <summary>
    /// A literal opening with an escape is the backslash then its text; any other is its text found in its span (a
    /// table cell's literal keeps the span of its trimmed spaces). Null when the text is not found there.
    /// </summary>
    private static int? LiteralOffset(string markdown, LiteralInline literal, string text, int chars)
    {
        if (literal.IsFirstCharacterEscaped)
        {
            return chars == 0 ? literal.Span.Start : literal.Span.Start + 1 + chars;
        }

        int found = text.Length > 0 ? FindInSpan(markdown, literal.Span, text) : -1;
        return found >= 0 ? found + chars : null;
    }

    private static int FindInSpan(string markdown, SourceSpan span, string text)
    {
        int start = Math.Clamp(span.Start, 0, markdown.Length);
        int length = Math.Clamp(span.End + 1, start, markdown.Length) - start;
        return markdown.IndexOf(text, start, length, StringComparison.Ordinal);
    }

    /// <summary>The page text of a code block is its lines joined with newlines, plus a final newline.</summary>
    private static int CodeOffset(CodeBlock code, int offset)
    {
        StringLineGroup lines = code.Lines;
        int remaining = offset;
        for (int i = 0; i < lines.Count; i++)
        {
            StringSlice slice = lines.Lines[i].Slice;
            if (remaining <= slice.Length)
            {
                return slice.Start + remaining;
            }

            remaining -= slice.Length + 1;
        }

        return lines.Count == 0 ? code.Span.Start : lines.Lines[lines.Count - 1].Slice.End + 1;
    }

    private static string CodeText(CodeBlock code)
    {
        var builder = new StringBuilder();
        StringLineGroup lines = code.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            builder.Append(lines.Lines[i].Slice.ToString()).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>A code block the page shows as <c>&lt;pre&gt;&lt;code&gt;</c> text, not a diagram, math or other mapped block.</summary>
    private static bool IsPlainCode(CodeBlock code) =>
        code is not (MathBlock or YamlFrontMatterBlock) && !(code is FencedCodeBlock { Info: { } info } && MappedInfos.Contains(info));

    /// <summary>
    /// The node's content in page order: untagged leaf inlines with their page text, and tagged children as opaque
    /// items. Untagged containers (an implicit paragraph, a raw HTML wrapper) are walked into.
    /// </summary>
    private static List<Item> Items(MarkdownObject node)
    {
        var items = new List<Item>();
        switch (node)
        {
            case LeafBlock { Inline: { } inline }:
                AddInlines(inline, items);
                break;
            case ContainerBlock container:
                AddBlocks(container, items);
                break;
            case ContainerInline inlines:
                AddInlines(inlines, items);
                break;
            case CodeInline code:
                items.Add(new Item(code, code.Content));
                break;
            case AutolinkInline autolink:
                items.Add(new Item(autolink, autolink.Url));
                break;
        }

        return items;
    }

    /// <summary>
    /// Adds a container's blocks; returns false once it reaches a definition list, which ends the content as far as
    /// the walk goes: the page snaps points in one outward, so neither its text nor anything after it is counted.
    /// </summary>
    private static bool AddBlocks(ContainerBlock container, List<Item> items)
    {
        foreach (Block child in container)
        {
            if (child is DefinitionList)
            {
                items.Add(new Item(child, null));
                return false;
            }

            if (IsTaggedInHtml(child))
            {
                items.Add(new Item(child, null));
            }
            else if (child is LeafBlock { Inline: { } inline })
            {
                AddInlines(inline, items);
            }
            else if (child is ContainerBlock inner && !AddBlocks(inner, items))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddInlines(ContainerInline container, List<Item> items)
    {
        for (Inline? inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            if (IsTaggedInHtml(inline))
            {
                items.Add(new Item(inline, null));
            }
            else if (inline is ContainerInline inner)
            {
                AddInlines(inner, items);
            }
            else
            {
                items.Add(new Item(inline, PageText(inline)));
            }
        }
    }

    /// <summary>The text an untagged leaf inline puts in the page.</summary>
    private static string PageText(Inline inline) =>
        inline switch
        {
            LiteralInline literal => literal.Content.ToString(),
            HtmlEntityInline entity => entity.Transcoded.ToString(),
            LineBreakInline => "\n",
            FootnoteLink { IsBackLink: true } => "↩",
            FootnoteLink link => link.Footnote.Order.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

    private static bool IsMark(Inline inline) => inline is EmphasisInline or CodeInline or LinkInline or AutolinkInline or MathInline;

    /// <summary>Widens each end that falls inside a mark to the mark's whole span, until no end moves.</summary>
    private static (int Start, int End) WidenToMarks(MarkdownDocument document, int start, int end)
    {
        List<Inline> marks = [.. document.Descendants<Inline>().Where(IsMark)];
        bool moved = true;
        while (moved)
        {
            moved = false;
            foreach (Inline mark in marks)
            {
                (int newStart, int newEnd) = (WidenStart(mark, start), WidenEnd(mark, end));
                moved |= newStart != start || newEnd != end;
                (start, end) = (newStart, newEnd);
            }
        }

        return (start, end);
    }

    /// <summary>
    /// A start inside a mark widens to the mark's start, or, when it sits in the closing delimiter after the mark's last
    /// child (marks ending together), moves forward past the mark.
    /// </summary>
    private static int WidenStart(Inline mark, int start)
    {
        if (start <= mark.Span.Start || start > mark.Span.End)
        {
            return start;
        }

        return mark is ContainerInline { LastChild: { } last } && start > last.Span.End ? mark.Span.End + 1 : mark.Span.Start;
    }

    /// <summary>
    /// An end inside a mark widens to the mark's end, or, when it sits in the opening delimiter before the mark's first
    /// child (marks starting together), moves back before the mark.
    /// </summary>
    private static int WidenEnd(Inline mark, int end)
    {
        if (end <= mark.Span.Start || end > mark.Span.End)
        {
            return end;
        }

        return mark is ContainerInline { FirstChild: { } first } && end <= first.Span.Start ? mark.Span.Start : mark.Span.End + 1;
    }

    /// <summary>The leaf blocks a selection end can widen in: paragraphs, headings and code blocks.</summary>
    private static bool IsWideningLeaf(LeafBlock block) =>
        block switch
        {
            ParagraphBlock or HeadingBlock => true,
            YamlFrontMatterBlock => false,
            CodeBlock => true,
            _ => false,
        };

    /// <summary>
    /// A table cell's or a footnote's paragraph, whose line starts with the row's or the footnote's own syntax: it never
    /// widens to take that syntax or to run past the cell.
    /// </summary>
    private static bool IsFramedParagraph(LeafBlock block) => block is ParagraphBlock { Parent: TableCell or Footnote };

    /// <summary>
    /// A start at or before a leaf block's content moves to the start of its first source line, markers included; a start
    /// at or after its content's end moves to the block's end, so a closing fence or a heading's closing marks stay out.
    /// </summary>
    private static int WidenStartToBlock(List<LeafBlock> leaves, string markdown, int start, int end)
    {
        LeafBlock? block = leaves.Find(leaf => leaf.Span.Start <= start && start <= leaf.Span.End + 1);
        if (block is null)
        {
            return start;
        }

        if (IsFramedParagraph(block))
        {
            return WidenStartAtTableEdge(block, markdown, start, end);
        }

        if (start <= ContentStart(block, markdown))
        {
            return LineStart(markdown, block.Span.Start);
        }

        return start >= ContentEnd(block) ? block.Span.End + 1 : start;
    }

    /// <summary>
    /// An end at or before a leaf block's content (where a triple click ends) moves back before the block, so the next
    /// block's marker stays out; an end at or after its content moves to the block's end, a closing fence included.
    /// </summary>
    private static int WidenEndToBlock(List<LeafBlock> leaves, string markdown, int end, int start)
    {
        LeafBlock? block = leaves.Find(leaf => leaf.Span.Start <= end && end <= leaf.Span.End + 1);
        if (block is null)
        {
            return end;
        }

        if (end <= ContentStart(block, markdown))
        {
            return block is ParagraphBlock { Parent: TableCell cell } ? BeforeCell(markdown, cell, end) : LineStart(markdown, block.Span.Start);
        }

        return WidenEndPastContent(block, markdown, end, start);
    }

    /// <summary>
    /// An end at or after a leaf block's content end moves to the block's end, a closing fence included; in a table's
    /// last cell it moves to the table's end when the selection starts before that cell's text, so text selected within
    /// the cell stays just that text. Any other framed paragraph keeps the end, so it never runs past its cell.
    /// </summary>
    private static int WidenEndPastContent(LeafBlock block, string markdown, int end, int start)
    {
        if (IsTableEdge(block, last: true))
        {
            bool beyondCell = end >= CellTextEnd(block, markdown) && start < ContentStart(block, markdown);
            return beyondCell ? TableEnd(block, markdown) : end;
        }

        return end < ContentEnd(block) || IsFramedParagraph(block) ? end : block.Span.End + 1;
    }

    /// <summary>
    /// Where a table cell's text ends: a pipe table's cell spans the spaces and the <c>|</c> after its text, so they are
    /// stepped back over.
    /// </summary>
    private static int CellTextEnd(LeafBlock block, string markdown)
    {
        int start = ContentStart(block, markdown);
        int end = ContentEnd(block);
        while (end > start && markdown[end - 1] is ' ' or '\t' or '|')
        {
            end--;
        }

        return end;
    }

    /// <summary>
    /// A start in a framed paragraph moves only at a table's edges: at or before its first cell's content to the table's
    /// line start (a grid table's top border included) when the selection ends past that cell's text, and at or after its
    /// last cell's content past the table.
    /// </summary>
    private static int WidenStartAtTableEdge(LeafBlock block, string markdown, int start, int end)
    {
        if (IsTableEdge(block, last: false) && start <= ContentStart(block, markdown) && end > CellTextEnd(block, markdown))
        {
            return LineStart(markdown, TableOf(block).Span.Start);
        }

        return IsTableEdge(block, last: true) && start >= CellTextEnd(block, markdown) ? TableEnd(block, markdown) : start;
    }

    /// <summary>
    /// Whether the block is the first block of its table's first cell, or (<paramref name="last"/>) the last block of its
    /// last cell.
    /// </summary>
    private static bool IsTableEdge(LeafBlock block, bool last)
    {
        if (block.Parent is not TableCell { Parent: TableRow { Parent: Table table } row } cell)
        {
            return false;
        }

        return last
            ? ReferenceEquals(cell[^1], block) && ReferenceEquals(row[^1], cell) && ReferenceEquals(table[^1], row)
            : ReferenceEquals(cell[0], block) && ReferenceEquals(row[0], cell) && ReferenceEquals(table[0], row);
    }

    private static Table TableOf(LeafBlock block) => (Table)block.Parent!.Parent!.Parent!;

    /// <summary>
    /// Where the block's table ends: the end of the source line its span ends on, since a pipe table's span stops before its
    /// last row's closing pipe.
    /// </summary>
    private static int TableEnd(LeafBlock block, string markdown)
    {
        int lineEnd = markdown.IndexOf('\n', TableOf(block).Span.End);
        return lineEnd < 0 ? markdown.Length : lineEnd;
    }

    /// <summary>
    /// An end at a cell's start moves back over the spaces and the one <c>|</c> before it, and to the line start when only
    /// pipes and spaces are left before it on its line (the row's first cell). An end at the table's first cell moves
    /// to the table's line start, before a grid table's top border.
    /// </summary>
    private static int BeforeCell(string markdown, TableCell cell, int end)
    {
        if (IsTablesFirstCell(cell))
        {
            return LineStart(markdown, cell.Parent!.Parent!.Span.Start);
        }

        int at = end;
        while (at > 0 && markdown[at - 1] is ' ' or '\t')
        {
            at--;
        }

        if (at > 0 && markdown[at - 1] == '|')
        {
            at--;
        }

        int line = LineStart(markdown, at);
        return markdown.AsSpan(line, at - line).Trim(" \t|").IsEmpty ? line : at;
    }

    /// <summary>True when the cell is the first cell of its table's first row.</summary>
    private static bool IsTablesFirstCell(TableCell cell) =>
        cell is { Parent: TableRow row } && row.IndexOf(cell) == 0 && row.Parent is Table table && table.IndexOf(row) == 0;

    private static int LineStart(string markdown, int offset) => offset == 0 ? 0 : markdown.LastIndexOf('\n', offset - 1) + 1;

    /// <summary>Where a block's text starts: its first code line, or its first positioned inline after a task checkbox and spaces.</summary>
    private static int ContentStart(LeafBlock block, string markdown)
    {
        if (block is CodeBlock { Lines.Count: > 0 } code)
        {
            return code.Lines.Lines[0].Slice.Start;
        }

        int start = TopInlines(block).FirstOrDefault(inline => inline is not TaskList)?.Span.Start ?? block.Span.Start;
        while (start <= block.Span.End && start < markdown.Length && markdown[start] is ' ' or '\t')
        {
            start++;
        }

        return start;
    }

    private static int ContentEnd(LeafBlock block)
    {
        if (block is CodeBlock { Lines.Count: > 0 } code)
        {
            return code.Lines.Lines[code.Lines.Count - 1].Slice.End + 1;
        }

        Inline? last = TopInlines(block).LastOrDefault();
        return last is null ? block.Span.End + 1 : last.Span.End + 1;
    }

    /// <summary>The block's top-level inlines that have a source position.</summary>
    private static IEnumerable<Inline> TopInlines(LeafBlock block)
    {
        for (Inline? inline = block.Inline?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            if (!inline.Span.IsEmpty)
            {
                yield return inline;
            }
        }
    }

    private static int Depth(MarkdownObject node)
    {
        int depth = 0;
        for (MarkdownObject? current = Parent(node); current is not null; current = Parent(current))
        {
            depth++;
        }

        return depth;
    }

    private static MarkdownObject? Parent(MarkdownObject node) =>
        node switch
        {
            Block block => block.Parent,
            ContainerInline { Parent: null } root => root.ParentBlock,
            Inline inline => inline.Parent,
            _ => null,
        };

    private static FrozenSet<string> ReadMappedInfos()
    {
        var renderer = new HtmlRenderer(new StringWriter(CultureInfo.InvariantCulture));
        MarkdownRenderer.Pipeline.Setup(renderer);
        CodeBlockRenderer? code = renderer.ObjectRenderers.OfType<CodeBlockRenderer>().FirstOrDefault();
        return code is null ? [] : code.BlocksAsDiv.Concat(code.BlockMapping.Keys).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsTaggedBlockType(MarkdownObject node) =>
        node
            is HeadingBlock
                or QuoteBlock
                or ListBlock
                or ListItemBlock
                or CodeBlock
                or ThematicBreakBlock
                or Table
                or TableRow
                or TableCell
                or Figure
                or FigureCaption
                or FooterBlock
                or CustomContainer;

    private static bool IsTaggedInlineType(Inline inline) =>
        inline is EmphasisInline or LinkInline or CodeInline or AutolinkInline or AbbreviationInline or TaskList or MathInline;

    private static bool IsInsideDefinitionList(MarkdownObject node)
    {
        for (MarkdownObject? current = node; current is not null; current = Parent(current))
        {
            if (current is DefinitionList)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An image writes its children as alt text with HTML off, so nothing inside it gets an element.</summary>
    private static bool IsInsideImage(Inline inline)
    {
        for (ContainerInline? parent = inline.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is LinkInline { IsImage: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether Markdig writes the paragraph without <c>&lt;p&gt;</c>: the nearest ancestor that sets the renderer's
    /// implicit-paragraph state decides (a tight list's item, a single-block table cell, a footer; a quote or alert
    /// turns it off).
    /// </summary>
    private static bool RendersImplicitly(ParagraphBlock paragraph)
    {
        for (ContainerBlock? parent = paragraph.Parent; parent is not null; parent = parent.Parent)
        {
            bool? decided = ImplicitParagraphState(parent);
            if (decided is not null)
            {
                return decided.Value;
            }
        }

        return false;
    }

    private static bool? ImplicitParagraphState(ContainerBlock parent) =>
        parent switch
        {
            ListItemBlock item => item.Parent is ListBlock { IsLoose: false },
            TableCell { Count: 1 } => true,
            FooterBlock => true,
            QuoteBlock => false,
            _ => null,
        };

    /// <summary>One piece of a node's content: an untagged inline with its page text, or a tagged child (null text).</summary>
    private readonly record struct Item(MarkdownObject Node, string? Text);
}
