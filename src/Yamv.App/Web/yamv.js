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

  function highlightCode() {
    if (!window.hljs) {
      return;
    }
    window.hljs.configure({ ignoreUnescapedHTML: true });
    for (const code of content.querySelectorAll("pre > code")) {
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

  async function renderMath() {
    const nodes = content.querySelectorAll("span.math, div.math");
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

  async function renderMermaid() {
    const nodes = Array.from(content.querySelectorAll("pre.mermaid"));
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

  function restoreScroll(scroll, fragment) {
    if (fragment) {
      const target = document.getElementById(decodeFragment(fragment));
      if (target) {
        target.scrollIntoView({ block: "start" });
        return;
      }
    }
    const fraction = Math.min(1, Math.max(0, Number(scroll) || 0));
    window.scrollTo(0, fraction * scrollableHeight());
  }

  // Swaps in a document at options.contentWidth ("760px" or "none"), when given. The HTML is parsed into an inert
  // template first, so nothing in it loads before it is sanitized and its images are checked. Posts
  // { type: "rendered", generation } when math, diagrams and the scroll position are done (or the render failed),
  // unless a newer render has started; the host's generation numbers it.
  async function render(bodyHtml, options) {
    const settings = options || {};
    const generation = Number.isInteger(settings.generation) ? settings.generation : renderGeneration + 1;
    renderGeneration = generation;
    rendering = true;
    window.clearTimeout(scrollTimer);
    findState = { query: findState.query, marks: [], index: -1 };
    content.classList.add("yamv-pending");
    // The incoming document's width is set only once the outgoing one is hidden, so the outgoing one never reflows at it.
    if (typeof settings.contentWidth === "string") {
      setContentWidth(settings.contentWidth);
    }
    try {
      const template = document.createElement("template");
      template.innerHTML = bodyHtml;
      sanitize(template.content);
      rewriteImages(template.content, settings.docBase, settings.rootBase);
      content.replaceChildren(template.content);
      highlightCode();
      const pending = [];
      if (settings.hasMath) {
        pending.push(renderMath().catch((error) => reportError("math", error)));
      }
      if (settings.hasMermaid) {
        pending.push(renderMermaid().catch((error) => reportError("mermaid", error)));
      }
      await Promise.all(pending);
      if (generation !== renderGeneration) {
        return;
      }
      restoreScroll(settings.scroll, settings.fragment);
      if (!findBar.hidden && findState.query) {
        find(findState.query, "first");
      }
    } catch (error) {
      reportError("render", error);
    } finally {
      if (generation === renderGeneration) {
        content.classList.remove("yamv-pending");
        post({ type: "rendered", generation });
        window.requestAnimationFrame(() => {
          if (generation === renderGeneration) {
            rendering = false;
          }
        });
      }
    }
  }

  function setTheme(cssText) {
    themeStyle.textContent = cssText;
    if (mermaidReady && content.querySelector("pre.mermaid")) {
      renderMermaid().catch((error) => reportError("mermaid", error));
    }
  }

  function setContentWidth(value) {
    document.documentElement.style.setProperty("--yamv-content-width", value);
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
      const previous = findState.marks[findState.index];
      if (previous) {
        previous.classList.remove("yamv-find-current");
      }
      if (direction === "prev") {
        findState.index = findState.index <= 0 ? total - 1 : findState.index - 1;
      } else if (direction === "next") {
        findState.index = (findState.index + 1) % total;
      } else {
        findState.index = 0;
      }
      const currentMark = findState.marks[findState.index];
      currentMark.classList.add("yamv-find-current");
      currentMark.scrollIntoView({ block: "center" });
    }
    const result = { current: total > 0 ? findState.index + 1 : 0, total };
    showCount(result);
    post({ type: "find", current: result.current, total: result.total });
    return result;
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

  // The keys the host may want as shortcuts: an F-key, or Ctrl/Cmd with a letter, a digit, a physical bracket or Tab.
  function isShortcut(key, command, bracket) {
    if (/^F([1-9]|1[0-9]|2[0-4])$/.test(key)) {
      return true;
    }
    if (bracket) {
      return true;
    }
    return command && (/^[\p{L}\p{Nd}]$/u.test(key) || key === "Tab");
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

  function scrollToFragment(fragment) {
    const target = document.getElementById(decodeFragment(fragment));
    if (target) {
      target.scrollIntoView({ block: "start" });
    }
  }

  // A primary click (click) or a middle click (auxclick, button 1) on a document link; other buttons do nothing.
  function onLinkClick(event) {
    if (event.type === "auxclick" && event.button !== 1) {
      return;
    }
    const anchor = event.target instanceof Element ? event.target.closest("a[href]") : null;
    if (!anchor || !content.contains(anchor)) {
      return;
    }
    event.preventDefault();
    const href = anchor.getAttribute("href");
    if (href.startsWith("#")) {
      if (event.button === 0) {
        scrollToFragment(href.slice(1));
      }
      return;
    }
    post({ type: "link", href, button: event.button });
  }

  document.addEventListener("click", onLinkClick);
  document.addEventListener("auxclick", onLinkClick);

  window.addEventListener("scroll", () => {
    if (rendering) {
      return;
    }
    window.clearTimeout(scrollTimer);
    scrollTimer = window.setTimeout(() => {
      const height = scrollableHeight();
      post({ type: "scroll", value: height > 0 ? window.scrollY / height : 0, generation: renderGeneration });
    }, 150);
  }, { passive: true });

  document.addEventListener("securitypolicyviolation", (event) => {
    post({ type: "error", where: "csp", message: `${event.effectiveDirective} blocked ${event.blockedURI || "inline code"}` });
  });

  window.yamv = { render, setTheme, setContentWidth, find, clearFind, openFind };
  post({ type: "ready" });
})();
