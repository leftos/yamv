// YAMV document page: renders the HTML the host sends, runs the code, math and diagram renderers,
// owns find-in-page and its bar, and reports scroll, links, find results and app shortcuts to the host.
"use strict";

(() => {
  const content = document.getElementById("yamv-content");
  const themeStyle = document.getElementById("yamv-theme");
  const findBar = document.getElementById("yamv-findbar");
  const findField = document.getElementById("yamv-find-field");
  const findCount = document.getElementById("yamv-find-count");

  const scriptLoads = new Map();
  let renderGeneration = 0;
  let rendering = false;
  let scrollTimer = 0;
  let mermaidReady = false;
  let findState = { query: "", marks: [], index: -1 };
  // The documentPath of the document the content shows once its render finished; null while none is fully shown.
  let shownDocument = null;
  // The hidden element a re-render of the shown document is built in, while the old page stays on screen.
  let staging = null;
  // The width the staged document lands at: its render's, or a later live change's.
  let stagedWidth = null;
  // Counts theme changes, so a render can tell its diagrams were drawn in an older theme.
  let themeGeneration = 0;

  function post(message) {
    if (typeof window.invokeCSharpAction === "function") {
      window.invokeCSharpAction(JSON.stringify(message));
    }
  }

  function reportError(where, error) {
    post({ type: "error", where, message: String(error && error.message ? error.message : error) });
  }

  function loadScript(src) {
    if (!scriptLoads.has(src)) {
      scriptLoads.set(src, new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = src;
        script.onload = () => resolve();
        script.onerror = () => reject(new Error(`could not load ${src}`));
        document.head.appendChild(script);
      }));
    }
    return scriptLoads.get(src);
  }

  function loadStylesheet(href) {
    if (!document.querySelector(`link[href="${href}"]`)) {
      const link = document.createElement("link");
      link.rel = "stylesheet";
      link.href = href;
      document.head.appendChild(link);
    }
  }

  // ---------- render ----------

  // What the document may not bring into the page: style elements and attributes would restyle the page itself,
  // meta, base and link elements could refresh it, re-base its URLs or load resources, and frames, objects and
  // embeds would load other documents into it.
  function sanitize(fragment) {
    const removed = "style, meta, base, link, iframe, frame, frameset, object, embed, portal, fencedframe";
    for (const element of fragment.querySelectorAll(removed)) {
      element.remove();
    }
    for (const element of fragment.querySelectorAll("[style]")) {
      element.removeAttribute("style");
    }
  }

  // Decides where an image URL from the document may load from: http(s) as written (a leading "//" means https:),
  // data:image/ URLs, and file: URLs, relative ones resolved against the document's folder, only without a host and inside the group
  // root. Returns { url } or { reason }. The group root check catches broken or surprising paths; it is not the
  // security boundary. The boundary is the page's Content-Security-Policy: no script from the document can run.
  function checkImageUrl(raw, docBase, rootBase) {
    if (!raw) {
      return { reason: "empty" };
    }
    let url;
    try {
      url = new URL(raw.startsWith("//") ? `https:${raw}` : raw, docBase || undefined);
    } catch (error) {
      return { reason: "unparsable" };
    }
    if (url.protocol === "http:" || url.protocol === "https:") {
      return { url: url.href };
    }
    if (url.protocol === "data:") {
      return /^data:image\//i.test(url.href) ? { url: url.href } : { reason: "data URL that is not an image" };
    }
    if (url.protocol !== "file:") {
      return { reason: `scheme ${url.protocol}` };
    }
    if (url.host) {
      return { reason: "file URL with a host" };
    }
    if (!rootBase || !url.href.toLowerCase().startsWith(rootBase.toLowerCase())) {
      return { reason: "outside group root" };
    }
    return { url: url.href };
  }

  // Rewrites each srcset candidate ("url [descriptor]") by checkImageUrl, dropping the ones it blocks.
  function rewriteSrcset(element, docBase, rootBase) {
    const kept = [];
    for (const candidate of element.getAttribute("srcset").split(",")) {
      const [raw, ...descriptors] = candidate.trim().split(/\s+/);
      if (!raw) {
        continue;
      }
      const checked = checkImageUrl(raw, docBase, rootBase);
      if (checked.url) {
        kept.push([checked.url, ...descriptors].join(" "));
      } else {
        post({ type: "blocked", src: raw, reason: checked.reason });
      }
    }
    if (kept.length > 0) {
      element.setAttribute("srcset", kept.join(", "));
    } else {
      element.removeAttribute("srcset");
    }
  }

  // Every element attribute that loads a URL into the page, besides srcset. Frames, objects and embeds are removed
  // by sanitize instead.
  const urlAttributes = [
    ["img", "src"], ["input", "src"], ["video", "poster"], ["video", "src"], ["audio", "src"], ["source", "src"],
    ["track", "src"], ["image", "href"], ["image", "xlink:href"], ["use", "href"], ["use", "xlink:href"],
    ["feImage", "href"], ["feImage", "xlink:href"], ["[background]", "background"],
  ];
  const svgNamespace = "http://www.w3.org/2000/svg";

  function rewriteUrlAttribute(element, attribute, docBase, rootBase) {
    const raw = element.getAttribute(attribute);
    // An SVG "#id" reference points inside the document and loads nothing.
    if (element.namespaceURI === svgNamespace && raw.startsWith("#")) {
      return;
    }
    const checked = checkImageUrl(raw, docBase, rootBase);
    if (checked.url) {
      element.setAttribute(attribute, checked.url);
    } else {
      element.removeAttribute(attribute);
      post({ type: "blocked", src: raw, reason: checked.reason });
    }
  }

  function rewriteImages(fragment, docBase, rootBase) {
    for (const [selector, attribute] of urlAttributes) {
      for (const element of fragment.querySelectorAll(selector)) {
        if (element.hasAttribute(attribute)) {
          rewriteUrlAttribute(element, attribute, docBase, rootBase);
        }
      }
    }
    for (const element of fragment.querySelectorAll("img[srcset], source[srcset]")) {
      rewriteSrcset(element, docBase, rootBase);
    }
  }

  // Fence languages whose names highlight.js does not know as written.
  const languageAliases = new Map([["language-c#", "language-csharp"], ["language-f#", "language-fsharp"]]);

  function highlightCode(root) {
    if (!window.hljs) {
      return;
    }
    window.hljs.configure({ ignoreUnescapedHTML: true });
    for (const code of root.querySelectorAll("pre > code")) {
      for (const [written, known] of languageAliases) {
        code.classList.replace(written, known);
      }
      try {
        window.hljs.highlightElement(code);
      } catch (error) {
        reportError("highlight", error);
      }
    }
  }

  function stripMathDelimiters(text, display) {
    const trimmed = text.trim();
    const open = display ? "\\[" : "\\(";
    const close = display ? "\\]" : "\\)";
    if (trimmed.startsWith(open) && trimmed.endsWith(close)) {
      return trimmed.slice(2, -2);
    }
    return trimmed;
  }

  async function renderMath(root) {
    const nodes = root.querySelectorAll("span.math, div.math");
    if (nodes.length === 0) {
      return;
    }
    loadStylesheet("vendor/katex/katex.min.css");
    await loadScript("vendor/katex/katex.min.js");
    for (const node of nodes) {
      const display = node.tagName === "DIV";
      const tex = stripMathDelimiters(node.textContent, display);
      try {
        window.katex.render(tex, node, { displayMode: display, throwOnError: false, trust: false });
      } catch (error) {
        node.classList.add("yamv-render-error");
        reportError("math", error);
      }
    }
  }

  function cssToken(name) {
    return getComputedStyle(document.documentElement).getPropertyValue(`--yamv-${name}`).trim();
  }

  function mermaidConfig() {
    return {
      startOnLoad: false,
      securityLevel: "strict",
      theme: "base",
      themeVariables: {
        background: cssToken("page-background"),
        fontFamily: `${cssToken("ui-font")}, "Inter V", sans-serif`,
        primaryColor: cssToken("code-background"),
        primaryTextColor: cssToken("text"),
        primaryBorderColor: cssToken("line"),
        secondaryColor: cssToken("selection-background"),
        secondaryTextColor: cssToken("text"),
        secondaryBorderColor: cssToken("accent-soft"),
        tertiaryColor: cssToken("hover-background"),
        tertiaryTextColor: cssToken("text"),
        tertiaryBorderColor: cssToken("line"),
        lineColor: cssToken("text-muted"),
        textColor: cssToken("text"),
        mainBkg: cssToken("code-background"),
        nodeBorder: cssToken("accent"),
        clusterBkg: cssToken("chrome-background"),
        clusterBorder: cssToken("line"),
        titleColor: cssToken("heading"),
        edgeLabelBackground: cssToken("page-background"),
        noteBkgColor: cssToken("warning-background"),
        noteTextColor: cssToken("text"),
        noteBorderColor: cssToken("warning-line"),
      },
    };
  }

  async function renderMermaid(root) {
    const nodes = Array.from(root.querySelectorAll("pre.mermaid"));
    if (nodes.length === 0) {
      return;
    }
    await loadScript("vendor/mermaid/mermaid.min.js");
    for (const node of nodes) {
      if (!node.hasAttribute("data-yamv-source")) {
        node.setAttribute("data-yamv-source", node.textContent);
      }
      node.removeAttribute("data-processed");
      node.textContent = node.getAttribute("data-yamv-source");
    }
    window.mermaid.initialize(mermaidConfig());
    mermaidReady = true;
    try {
      await window.mermaid.run({ nodes, suppressErrors: true });
    } catch (error) {
      reportError("mermaid", error);
    }
  }

  function scrollableHeight() {
    return Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
  }

  function decodeFragment(fragment) {
    try {
      return decodeURIComponent(fragment);
    } catch (error) {
      reportError("fragment", error);
      return fragment;
    }
  }

  // ---------- scroll anchor ----------

  // Core tags every top-level block with the zero-based source line it starts on (data-line): on the block itself, or on
  // a fenced block's code element. Only those count, and only a line that is a non-negative 32-bit integer, so raw HTML
  // in the document carrying a data-line of its own can neither become an anchor nor break the scroll message.
  function lineBlocks(root) {
    const candidates = root.querySelectorAll(":scope > [data-line], :scope > pre > code[data-line]");
    return Array.from(candidates).filter((block) => lineOf(block) !== null);
  }

  function lineOf(block) {
    const text = block.getAttribute("data-line");
    const line = /^\d{1,10}$/.test(text) ? Number(text) : -1;
    return line >= 0 && line <= 2147483647 ? line : null;
  }

  // The box a block takes on screen: a fenced block's line is on its code element, inside the pre that draws it.
  function boxOf(block) {
    return block.tagName === "CODE" ? block.parentElement : block;
  }

  // A block's text as a reload compares it: a diagram's is its source, since the svg it renders to differs every time.
  function blockText(block) {
    return block.getAttribute("data-yamv-source") ?? block.textContent;
  }

  // Each anchor block of the content with its source line and text, built once per landing.
  function describeBlocks() {
    return lineBlocks(content).map((block) => ({ block, line: lineOf(block), text: blockText(block) }));
  }

  // The block at the top of the view, the first whose box reaches below the view's top edge, with the fraction (0 to 1)
  // of its height above that edge; null when no block does.
  function topAnchor() {
    for (const block of lineBlocks(content)) {
      const box = boxOf(block).getBoundingClientRect();
      if (box.bottom > 0) {
        return { block, offset: box.height > 0 ? Math.min(1, Math.max(0, -box.top / box.height)) : 0 };
      }
    }
    return null;
  }

  // Where a block of the page about to be replaced stands: its source line and its text.
  function blockMark(block) {
    return { line: lineOf(block), text: blockText(block) };
  }

  // The block of the new content with the same text as the old one, the nearest by source line when several have it.
  function sameTextBlock(blocks, mark) {
    let best = null;
    for (const entry of blocks) {
      if (entry.text === mark.text && (!best || Math.abs(entry.line - mark.line) < Math.abs(best.line - mark.line))) {
        best = entry;
      }
    }
    return best;
  }

  function blockAtOrAfter(blocks, line) {
    return blocks.find((entry) => entry.line >= line) || null;
  }

  // The block an old block maps to in the new content: the one with its text, else the first at or after its line.
  function mappedBlock(blocks, mark) {
    return sameTextBlock(blocks, mark) || blockAtOrAfter(blocks, mark.line);
  }

  // The anchor the host sent: options.anchorLine and options.anchorOffset; null without a line.
  function hostAnchor(settings) {
    if (!Number.isInteger(settings.anchorLine)) {
      return null;
    }
    return { line: settings.anchorLine, text: null, offset: Math.min(1, Math.max(0, Number(settings.anchorOffset) || 0)) };
  }

  // Where to bring to the top of the view, { entry, offset }; null sends the caller to the scroll fraction. A reload maps
  // the old page's top block, any other show the host's anchor. The offset within the block holds when the block found
  // has the anchor's text or line.
  function anchorTarget(settings, reloadAnchor, blocks) {
    const mark = reloadAnchor || hostAnchor(settings);
    const entry = mark ? mappedBlock(blocks, mark) : null;
    if (!entry) {
      return null;
    }
    return { entry, offset: entry.text === mark.text || entry.line === mark.line ? mark.offset : 0 };
  }

  function scrollToAnchor(target, blocks) {
    const box = boxOf(target.entry.block).getBoundingClientRect();
    // The first block, from its top, comes back with the page's top padding above it, as the page opens.
    const opening = target.entry === blocks[0] && target.offset === 0;
    window.scrollTo(0, opening ? 0 : box.top + window.scrollY + target.offset * box.height);
  }

  // Lands the view: on the fragment's element; else, on a reload of a page that was at its very top or bottom, there
  // again; else on the anchor; else at options.scroll.
  function restoreScroll(settings, marks, blocks) {
    if (settings.fragment && scrollToId(settings.fragment)) {
      return;
    }
    if (marks.pin) {
      window.scrollTo(0, marks.pin === "top" ? 0 : scrollableHeight());
      return;
    }
    const target = anchorTarget(settings, marks.anchor, blocks);
    if (target) {
      scrollToAnchor(target, blocks);
      return;
    }
    const fraction = Math.min(1, Math.max(0, Number(settings.scroll) || 0));
    window.scrollTo(0, fraction * scrollableHeight());
  }

  function reportScroll() {
    const height = scrollableHeight();
    const top = topAnchor();
    post({
      type: "scroll",
      value: height > 0 ? window.scrollY / height : 0,
      line: top ? lineOf(top.block) : null,
      offset: top ? top.offset : null,
      generation: renderGeneration,
    });
  }

  // ---------- re-render of the shown document ----------

  // A hidden element beside the content, laid out at the incoming width, that a re-render of the shown document is built
  // in. Being fixed, it adds nothing to the page's scrollable height.
  function createStaging(width) {
    const element = document.createElement("div");
    element.className = "yamv-content md yamv-pending";
    element.setAttribute("aria-hidden", "true");
    element.style.position = "fixed";
    element.style.top = "0";
    element.style.left = "0";
    element.style.right = "0";
    staging = element;
    stagedWidth = typeof width === "string" ? width : null;
    if (stagedWidth) {
      element.style.setProperty("--yamv-content-width", stagedWidth);
    }
    content.after(element);
    return element;
  }

  function discardStaging() {
    if (staging) {
      staging.remove();
      staging = null;
    }
  }

  // "top" when the view is at the very top, "bottom" when its bottom is within 2 px of the document's end, else null.
  function pinOf() {
    if (window.scrollY === 0) {
      return "top";
    }
    return window.scrollY + window.innerHeight >= document.documentElement.scrollHeight - 2 ? "bottom" : null;
  }

  // The selected find match's block and its order among the block's matches; null when no match is selected.
  function findMarkOf() {
    const current = findState.marks[findState.index];
    const block = current ? lineBlocks(content).find((candidate) => candidate.contains(current)) : null;
    if (!block) {
      return null;
    }
    return { ...blockMark(block), order: findState.marks.filter((mark) => block.contains(mark)).indexOf(current) };
  }

  // What the old page tells a reload of it: a pin to its top or bottom, its top block and the offset within it, and the
  // selected find match.
  function takeReloadMarks() {
    const top = topAnchor();
    return {
      reload: true,
      pin: pinOf(),
      anchor: top ? { ...blockMark(top.block), offset: top.offset } : null,
      findMark: findMarkOf(),
    };
  }

  const showMarks = { reload: false, pin: null, anchor: null, findMark: null };

  // Moves the staged document into the content in one step, after taking the old page's marks, and sets the latest width
  // the host sent for it; nothing paints between.
  function swapIn(staged) {
    const marks = takeReloadMarks();
    const width = stagedWidth;
    findState = { query: findState.query, marks: [], index: -1 };
    const range = document.createRange();
    range.selectNodeContents(staged);
    pageDrag = false;
    content.replaceChildren(range.extractContents());
    discardStaging();
    if (width) {
      setContentWidth(width);
    }
    return marks;
  }

  function beginRender(settings) {
    renderGeneration = Number.isInteger(settings.generation) ? settings.generation : renderGeneration + 1;
    rendering = true;
    window.clearTimeout(scrollTimer);
    discardStaging();
    return renderGeneration;
  }

  // A render of another document hides the content, and only then sets the incoming document's width, so the outgoing
  // one never reflows at it.
  function hideForShow(settings) {
    shownDocument = null;
    findState = { query: findState.query, marks: [], index: -1 };
    content.classList.add("yamv-pending");
    if (typeof settings.contentWidth === "string") {
      setContentWidth(settings.contentWidth);
    }
    return content;
  }

  // Parses the HTML into an inert template, so nothing in it loads before it is sanitized and its images are checked,
  // and runs the code, math and diagram renderers on it.
  async function build(target, bodyHtml, settings, generation) {
    const theme = themeGeneration;
    const template = document.createElement("template");
    template.innerHTML = bodyHtml;
    sanitize(template.content);
    rewriteImages(template.content, settings.docBase, settings.rootBase);
    pageDrag = false;
    target.replaceChildren(template.content);
    highlightCode(target);
    const pending = [];
    if (settings.hasMath) {
      pending.push(renderMath(target).catch((error) => reportError("math", error)));
    }
    if (settings.hasMermaid) {
      pending.push(renderMermaid(target).catch((error) => reportError("mermaid", error)));
    }
    await Promise.all(pending);
    if (settings.hasMermaid) {
      await catchUpTheme(target, theme, generation);
    }
  }

  // Draws the diagrams again for as long as the theme changed while they were drawn, so they land in the current theme.
  async function catchUpTheme(target, theme, generation) {
    let drawn = theme;
    while (drawn !== themeGeneration && generation === renderGeneration) {
      drawn = themeGeneration;
      await renderMermaid(target).catch((error) => reportError("mermaid", error));
    }
  }

  function land(settings, marks) {
    const blocks = describeBlocks();
    restoreScroll(settings, marks, blocks);
    shownDocument = typeof settings.documentPath === "string" ? settings.documentPath : null;
    if (!findBar.hidden && findState.query) {
      reselectFind(marks, blocks);
    }
  }

  // Ends a render: drops its staging when it never swapped in and, unless a newer render has started, shows the content,
  // posts { type: "rendered", generation } and, once the frame that scrolls it has passed, a scroll report, so the host
  // keeps the anchor the new content landed on.
  function finishRender(generation, target) {
    if (target === staging) {
      discardStaging();
    }
    if (generation !== renderGeneration) {
      return;
    }
    content.classList.remove("yamv-pending");
    post({ type: "rendered", generation });
    window.requestAnimationFrame(() => {
      if (generation === renderGeneration) {
        rendering = false;
        reportScroll();
      }
    });
  }

  // Swaps in a document at options.contentWidth ("760px" or "none"), when given. A render of the document the page
  // already shows (the same options.documentPath) is built off screen while the old page stays, and lands pinned to the
  // top or bottom when the old page was there, else on the old page's top block, and keeps its find match without
  // scrolling. Any other render hides the content and lands on the block at options.anchorLine, options.anchorOffset of
  // the way down it, else at options.scroll. Posts { type: "rendered", generation } when math, diagrams and the scroll
  // position are done (or the render failed), unless a newer render has started; the host's generation numbers it.
  async function render(bodyHtml, options) {
    const settings = options || {};
    const generation = beginRender(settings);
    const reload = typeof settings.documentPath === "string" && settings.documentPath === shownDocument;
    const target = reload ? createStaging(settings.contentWidth) : hideForShow(settings);
    try {
      await build(target, bodyHtml, settings, generation);
      if (generation === renderGeneration) {
        land(settings, reload ? swapIn(target) : showMarks);
      }
    } catch (error) {
      reportError("render", error);
    } finally {
      finishRender(generation, target);
    }
  }

  function setTheme(cssText) {
    themeStyle.textContent = cssText;
    themeGeneration++;
    // A hidden content is mid-render, and its render draws its diagrams again in the new theme (catchUpTheme).
    if (mermaidReady && !content.classList.contains("yamv-pending") && content.querySelector("pre.mermaid")) {
      renderMermaid(content).catch((error) => reportError("mermaid", error));
    }
  }

  // Sets the content width live. A staged re-render takes it too, and lands at it rather than at its render's width.
  function setContentWidth(value) {
    document.documentElement.style.setProperty("--yamv-content-width", value);
    if (staging) {
      stagedWidth = value;
      staging.style.setProperty("--yamv-content-width", value);
    }
  }

  // ---------- find ----------

  function collectTextNodes() {
    const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT, {
      acceptNode(node) {
        const parent = node.parentElement;
        // Diagram and math sources are never searched, rendered or not: the rendered forms are svg and .katex.
        if (!parent || parent.closest("svg, .katex, script, style, pre.mermaid, .math")) {
          return NodeFilter.FILTER_REJECT;
        }
        return node.nodeValue ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT;
      },
    });
    const nodes = [];
    while (walker.nextNode()) {
      nodes.push(walker.currentNode);
    }
    return nodes;
  }

  function escapeRegExp(text) {
    return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  }

  // Searches the original text with a case-insensitive pattern, so match offsets are offsets in the text itself:
  // lower-casing first would shift them wherever a character's lower case is longer, as with "İ".
  function wrapMatches(query) {
    const pattern = new RegExp(escapeRegExp(query), "giu");
    const marks = [];
    for (const node of collectTextNodes()) {
      let current = node;
      let consumed = 0;
      for (const found of Array.from(node.nodeValue.matchAll(pattern))) {
        const start = found.index - consumed;
        const match = start > 0 ? current.splitText(start) : current;
        const rest = match.splitText(found[0].length);
        const mark = document.createElement("mark");
        mark.className = "yamv-find";
        match.parentNode.replaceChild(mark, match);
        mark.appendChild(match);
        marks.push(mark);
        current = rest;
        consumed = found.index + found[0].length;
      }
    }
    return marks;
  }

  function unwrapMarks() {
    const parents = new Set();
    for (const mark of content.querySelectorAll("mark.yamv-find")) {
      const parent = mark.parentNode;
      while (mark.firstChild) {
        parent.insertBefore(mark.firstChild, mark);
      }
      parent.removeChild(mark);
      parents.add(parent);
    }
    for (const parent of parents) {
      parent.normalize();
    }
  }

  function showCount(result) {
    findCount.textContent = findState.query ? (result.total ? `${result.current} of ${result.total}` : "No results") : "";
  }

  function find(query, direction) {
    const text = String(query || "");
    if (text !== findState.query || findState.marks.length === 0 || direction === "first") {
      unwrapMarks();
      findState = { query: text, marks: text ? wrapMatches(text) : [], index: -1 };
    }
    const total = findState.marks.length;
    if (total > 0) {
      let index = 0;
      if (direction === "prev") {
        index = findState.index <= 0 ? total - 1 : findState.index - 1;
      } else if (direction === "next") {
        index = (findState.index + 1) % total;
      }
      selectMatch(index, "center");
    }
    return reportFind();
  }

  // Makes match <index> the current one and scrolls it into view at <block> ("center"), or not at all when <block> is null.
  function selectMatch(index, block) {
    const previous = findState.marks[findState.index];
    if (previous) {
      previous.classList.remove("yamv-find-current");
    }
    findState.index = index;
    const currentMark = findState.marks[index];
    currentMark.classList.add("yamv-find-current");
    if (block) {
      currentMark.scrollIntoView({ block });
    }
  }

  function reportFind() {
    const total = findState.marks.length;
    const result = { current: total > 0 ? findState.index + 1 : 0, total };
    showCount(result);
    post({ type: "find", current: result.current, total: result.total });
    return result;
  }

  // The match a reload selects for the one selected before it. The old block is mapped to the new content by its text,
  // else by its line (the first block at or after it); in that block, the match of the same order, or its last match
  // when it has fewer now. With no match in that block, the first match in a block at or after the old block's line,
  // else the last match.
  function reloadedMatchIndex(previous, blocks) {
    const marks = findState.marks;
    const entry = mappedBlock(blocks, previous);
    const inBlock = entry ? marks.filter((mark) => entry.block.contains(mark)) : [];
    if (inBlock.length > 0) {
      return marks.indexOf(inBlock[Math.min(previous.order, inBlock.length - 1)]);
    }
    const below = marks.findIndex((mark) => {
      const owner = blocks.find((candidate) => candidate.block.contains(mark));
      return owner !== undefined && owner.line >= previous.line;
    });
    return below >= 0 ? below : marks.length - 1;
  }

  // Runs the open find's query again over a freshly rendered document. A reload selects the reader's match again (or
  // the first, when none was selected) without scrolling, so the scroll anchor decides where the view is; any other
  // render selects the first match and scrolls to it.
  function reselectFind(marks, blocks) {
    if (!marks.reload) {
      find(findState.query, "first");
      return;
    }
    unwrapMarks();
    findState = { query: findState.query, marks: wrapMatches(findState.query), index: -1 };
    if (findState.marks.length > 0) {
      selectMatch(marks.findMark ? reloadedMatchIndex(marks.findMark, blocks) : 0, null);
    }
    reportFind();
  }

  function clearFind() {
    unwrapMarks();
    findState = { query: "", marks: [], index: -1 };
    showCount({ current: 0, total: 0 });
  }

  function openFind() {
    findBar.hidden = false;
    findField.focus();
    findField.select();
  }

  function closeFind() {
    findBar.hidden = true;
    findField.value = "";
    clearFind();
  }

  findField.addEventListener("input", () => {
    if (findField.value) {
      find(findField.value, "first");
    } else {
      clearFind();
    }
  });
  findField.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      find(findField.value, event.shiftKey ? "prev" : "next");
    } else if (event.key === "Escape") {
      event.preventDefault();
      closeFind();
    }
  });
  document.getElementById("yamv-find-prev").addEventListener("click", () => find(findField.value, "prev"));
  document.getElementById("yamv-find-next").addEventListener("click", () => find(findField.value, "next"));
  document.getElementById("yamv-find-close").addEventListener("click", closeFind);

  // ---------- keys ----------

  const pageHandledKeys = new Set(["c", "a"]);
  const fieldEditingKeys = new Set(["v", "x", "z", "y"]);
  // The physical keys in the US bracket positions: a layout may print something else there.
  const bracketKeys = { BracketLeft: "[", BracketRight: "]" };
  const navigationKeys = new Set([
    "Home", "End", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Backspace", "Delete", "PageUp", "PageDown",
  ]);

  // The keys the host may want as shortcuts: an F-key, or Ctrl/Cmd with a letter, a digit, a physical bracket, the comma or Tab.
  function isShortcut(key, command, bracket) {
    if (/^F([1-9]|1[0-9]|2[0-4])$/.test(key)) {
      return true;
    }
    if (bracket) {
      return true;
    }
    return command && (/^[\p{L}\p{Nd}]$/u.test(key) || key === "," || key === "Tab");
  }

  // Ctrl/Cmd alone on a physical bracket key names that bracket, whatever the layout prints on the key.
  function bracketShortcut(event) {
    const bracket = bracketKeys[event.code];
    return bracket && (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey ? bracket : null;
  }

  // `key` follows the keyboard layout and `code` names the physical key, so a Greek or Cyrillic layout
  // reports its own letter where the host's shortcuts and the page's own keys know the Latin one.
  function shortcutKey(event, bracket) {
    if (bracket) {
      return bracket;
    }
    if (/^\p{L}$/u.test(event.key) && !/^[a-zA-Z]$/.test(event.key)) {
      const physical = /^Key([A-Z])$/.exec(event.code);
      if (physical) {
        return physical[1].toLowerCase();
      }
    }
    return event.key;
  }

  window.addEventListener("keydown", (event) => {
    // AltGr typing (Ctrl+Alt on Windows) and navigation or editing keys always reach the page as they are.
    if (event.getModifierState("AltGraph") || navigationKeys.has(event.key)) {
      return;
    }
    const bracket = bracketShortcut(event);
    const forwarded = shortcutKey(event, bracket);
    const key = forwarded.length === 1 ? forwarded.toLowerCase() : forwarded;
    const command = event.ctrlKey || event.metaKey;
    if (command && key === "f") {
      event.preventDefault();
      openFind();
      return;
    }
    if (event.key === "Escape" && !findBar.hidden) {
      event.preventDefault();
      closeFind();
      return;
    }
    if (command && pageHandledKeys.has(key)) {
      return;
    }
    if (command && event.target === findField && fieldEditingKeys.has(key)) {
      return;
    }
    if (isShortcut(key, command, bracket)) {
      event.preventDefault();
      post({ type: "key", key: forwarded, ctrl: event.ctrlKey, meta: event.metaKey, shift: event.shiftKey, alt: event.altKey });
    }
  }, true);

  // ---------- links and scroll ----------

  // Scrolls the element whose id is the already decoded id to the top of the view; false when the page has no such
  // element, which does nothing and posts nothing.
  function scrollToId(id) {
    const target = document.getElementById(id);
    if (target) {
      target.scrollIntoView({ block: "start" });
    }
    return target !== null;
  }

  let lastDragPost = -Infinity;
  let pageDrag = false;

  // A primary click (click) or a middle click (auxclick, button 1) on a document link; other buttons do nothing. The
  // second click of a double click is swallowed, so a link posts once.
  function onLinkClick(event) {
    if (event.type === "auxclick" && event.button !== 1) {
      return;
    }
    const anchor = event.target instanceof Element ? event.target.closest("a[href]") : null;
    if (!anchor || !content.contains(anchor)) {
      return;
    }
    event.preventDefault();
    if (event.detail > 1) {
      return;
    }
    const href = anchor.getAttribute("href");
    if (href.startsWith("#")) {
      if (event.button === 0) {
        scrollToId(decodeFragment(href.slice(1)));
      }
      return;
    }
    post({ type: "link", href, button: event.button, generation: renderGeneration });
  }

  document.addEventListener("click", onLinkClick);
  document.addEventListener("auxclick", onLinkClick);

  // Tells the host files are being dragged over the page, so it can raise its overlay. dragover counts as well as dragenter:
  // after a drop the next drag's first event is dragover. Neither is default-prevented: that would lose a drop's paths.
  function isOutsideFileDrag(event) {
    return !pageDrag && Array.from(event.dataTransfer.types).includes("Files");
  }

  function onFileDrag(event) {
    if (!isOutsideFileDrag(event)) {
      return;
    }
    const now = performance.now();
    if (now - lastDragPost < 200) {
      return;
    }
    lastDragPost = now;
    post({ type: "drag", over: true });
  }

  // A drag begun in the page, such as a selection holding an image, is no file arriving from outside.
  document.addEventListener("dragstart", () => {
    pageDrag = true;
  });
  // On the window, in the capture phase: a render can remove the source node, whose dragend then never bubbles.
  window.addEventListener("dragend", () => {
    pageDrag = false;
  }, true);
  document.addEventListener("dragenter", onFileDrag);
  document.addEventListener("dragover", onFileDrag);
  document.addEventListener("dragleave", (event) => {
    if (event.relatedTarget === null && isOutsideFileDrag(event)) {
      lastDragPost = -Infinity;
      post({ type: "drag", over: false });
    }
  });

  window.addEventListener("scroll", () => {
    if (rendering) {
      return;
    }
    window.clearTimeout(scrollTimer);
    scrollTimer = window.setTimeout(reportScroll, 150);
  }, { passive: true });

  document.addEventListener("securitypolicyviolation", (event) => {
    post({ type: "error", where: "csp", message: `${event.effectiveDirective} blocked ${event.blockedURI || "inline code"}` });
  });

  window.yamv = { render, setTheme, setContentWidth, find, clearFind, openFind, scrollToId };
  post({ type: "ready" });
})();
