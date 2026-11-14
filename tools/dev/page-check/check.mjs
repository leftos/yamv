// Loads the document page in headless Chromium with a fixture document, stubs the host bridge
// (window.invokeCSharpAction), and checks the page's DOM and the messages it posts. Exit status 1 when a check fails.
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { chromium } from "playwright";

const here = path.dirname(fileURLToPath(import.meta.url));
const templateUrl = pathToFileURL(path.resolve(here, "../../../src/Yamv.App/Web/template.html")).href;
const rootBase = pathToFileURL(here + path.sep).href;
const docBase = new URL("fixture/", rootBase).href;

const filler = Array.from({ length: 60 }, (_, index) => `<p>Filler paragraph ${index}.</p>`).join("\n");
const fixture = `
<p id="turkish">İstanbul is the city. The end.</p>
<p>
<img id="scheme-relative" src="//images.example/x.png">
<img id="file-host" src="file://host/x.png">
<img id="relative" src="img/a.png">
<img id="outside" src="../../outside.png">
<img id="data" src="data:image/png;base64,AAAA">
<img id="data-html" src="data:text/html,hi">
<img id="srcset" srcset="img/a.png 1x, file://host/b.png 2x, //cdn.example/c.png 3x">
</p>
<picture><source id="source-out" srcset="../../out.png"><source id="source-in" srcset="img/p.png 2x"><img id="picture-img" src="img/p.png"></picture>
<style>p { color: red; }</style>
<p id="styled" style="color: red">styled</p>
<meta http-equiv="refresh" content="0; url=https://refresh.example/">
<base href="https://base.example/">
<link rel="stylesheet" href="https://link.example/x.css">
<svg width="10" height="10"><image id="svg-image" href="file://host/x.svg"></image><image id="svg-xlink" xlink:href="file://host/y.svg"></image><use id="svg-use" href="#local"></use></svg>
<video id="video" poster="file://host/poster.png"></video>
<iframe id="frame" src="file://host/x"></iframe><object id="object" data="file://host/x"></object><embed id="embed" src="file://host/x">
<p><a id="link" href="other.md">other</a> <a id="host-link" href="file://host/share/doc.md">share</a> <a id="outside-link" href="../../../outside.md">outside</a></p>
<pre><code id="csharp" class="language-c#">public class Widget { private int count; }</code></pre>
<pre class="mermaid">graph TD; the --> x</pre>
<p><span class="math">\\(the\\)</span></p>
${filler}
<h2 id="café">Café</h2>
${filler}
`;

let failed = 0;
let total = 0;
function check(name, ok, detail) {
  total++;
  if (!ok) {
    failed++;
  }
  console.log(`${ok ? "PASS" : "FAIL"} ${name}${ok ? "" : ` (got ${JSON.stringify(detail)})`}`);
}

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 900, height: 400 } });
  page.setDefaultTimeout(5 * 60_000);
  const requested = [];
  await page.route(/^https?:/, (route) => {
    requested.push(route.request().url());
    return route.abort();
  });
  await page.addInitScript(() => {
    window.__messages = [];
    window.invokeCSharpAction = (body) => window.__messages.push(JSON.parse(body));
  });
  await page.goto(templateUrl);
  await page.waitForFunction(() => window.__messages.some((message) => message.type === "ready"));

  const messages = () => page.evaluate(() => window.__messages);
  const clearMessages = () => page.evaluate(() => { window.__messages = []; });
  // Resolves once some entry of `window.__messages` matches `predicate`, which runs in the page and receives `arg`, its
  // serialisable data (a type, a generation or a count): Playwright ships the function's source, never its closure.
  const waitForMessage = (predicate, arg) => page.waitForFunction(predicate, arg);
  // Resolves once the view has held its scroll position for three animation frames: a render, and a viewport resize
  // before it, reach the scroll a frame or two after the call that asked for them, so a report alone is not settled.
  const waitForScrollSettled = () =>
    page.waitForFunction(() => {
      const state = window.__settle;
      if (!state || state.y !== window.scrollY) {
        window.__settle = { y: window.scrollY, frames: 0 };
        return false;
      }
      state.frames += 1;
      if (state.frames < 3) {
        return false;
      }
      window.__settle = null;
      return true;
    });
  const attribute = (id, name) => page.evaluate(([i, n]) => document.getElementById(i).getAttribute(n), [id, name]);
  const blocked = (all, src) => all.find((message) => message.type === "blocked" && message.src === src);
  const render = (options) => page.evaluate(([html, o]) => window.yamv.render(html, o), [fixture, options]);
  const baseOptions = { scroll: 0, fragment: null, hasMermaid: false, hasMath: false, docBase, rootBase };

  // Step 7: a render posts { type: "rendered", generation } when it is done.
  await render({ ...baseOptions, generation: 7 });
  let all = await messages();
  check("render posts rendered with the host's generation", all.some((m) => m.type === "rendered" && m.generation === 7), all);

  // Step 2: image URLs.
  check("//host image becomes https:", (await attribute("scheme-relative", "src")) === "https://images.example/x.png", await attribute("scheme-relative", "src"));
  check("file://host image is dropped", (await attribute("file-host", "src")) === null, await attribute("file-host", "src"));
  check("file://host image is reported blocked", blocked(all, "file://host/x.png")?.reason === "file URL with a host", blocked(all, "file://host/x.png"));
  check("relative image resolves under the document folder", (await attribute("relative", "src")) === `${docBase}img/a.png`, await attribute("relative", "src"));
  check("image outside the group root is dropped", (await attribute("outside", "src")) === null && blocked(all, "../../outside.png")?.reason === "outside group root", blocked(all, "../../outside.png"));
  check("data:image/ image is kept", (await attribute("data", "src")) === "data:image/png;base64,AAAA", await attribute("data", "src"));
  check("data: URL that is not an image is dropped", (await attribute("data-html", "src")) === null && blocked(all, "data:text/html,hi")?.reason === "data URL that is not an image", blocked(all, "data:text/html,hi"));
  check("SVG <image href> to file://host is dropped", (await attribute("svg-image", "href")) === null && blocked(all, "file://host/x.svg")?.reason === "file URL with a host", await attribute("svg-image", "href"));
  check("SVG <image xlink:href> to file://host is dropped", (await attribute("svg-xlink", "xlink:href")) === null, await attribute("svg-xlink", "xlink:href"));
  check("SVG <use href=#id> is left alone", (await attribute("svg-use", "href")) === "#local", await attribute("svg-use", "href"));
  check("<video poster> to file://host is dropped", (await attribute("video", "poster")) === null && blocked(all, "file://host/poster.png")?.reason === "file URL with a host", await attribute("video", "poster"));
  const frames = await page.evaluate(() => document.querySelectorAll("#yamv-content iframe, #yamv-content object, #yamv-content embed").length);
  check("iframe, object and embed are removed", frames === 0, frames);
  check("srcset keeps only allowed candidates, rewritten", (await attribute("srcset", "srcset")) === `${docBase}img/a.png 1x, https://cdn.example/c.png 3x`, await attribute("srcset", "srcset"));
  check("blocked srcset candidate is reported", blocked(all, "file://host/b.png")?.reason === "file URL with a host", blocked(all, "file://host/b.png"));
  check("<source srcset> outside the root is dropped", (await attribute("source-out", "srcset")) === null, await attribute("source-out", "srcset"));
  check("<source srcset> inside the root is rewritten", (await attribute("source-in", "srcset")) === `${docBase}img/p.png 2x`, await attribute("source-in", "srcset"));

  // Step 2 (owner ruling) and step 5: what the document may not bring in.
  const stripped = await page.evaluate(() => ({
    style: document.querySelectorAll("#yamv-content style").length,
    styleAttribute: document.getElementById("styled").hasAttribute("style"),
    head: document.querySelectorAll("#yamv-content meta, #yamv-content base, #yamv-content link").length,
    baseUri: document.baseURI,
  }));
  check("<style> elements are removed", stripped.style === 0, stripped);
  check("style= attributes are removed", stripped.styleAttribute === false, stripped);
  check("meta, base and link elements are removed", stripped.head === 0, stripped);
  check("the document's <base> does not re-base the page", stripped.baseUri === templateUrl, stripped);
  check("meta refresh does not navigate", page.url() === templateUrl, page.url());
  check("no request to the refresh or link targets", !requested.some((url) => /refresh\.example|link\.example/.test(url)), requested);

  // Step 11: C# fences are highlighted as csharp.
  const code = await page.evaluate(() => {
    const element = document.getElementById("csharp");
    return { classes: element.className, keywords: element.querySelectorAll(".hljs-keyword").length };
  });
  check("language-c# is highlighted as csharp", code.classes.includes("language-csharp") && code.keywords > 0, code);

  // Step 4: only primary and middle clicks post links. A click posts synchronously, so the two clicks' messages are
  // there once the clicks return; a right click that posted would be first in that list.
  await clearMessages();
  await page.click("#link", { button: "right" });
  await page.keyboard.press("Escape");
  await page.click("#link", { button: "middle" });
  await page.click("#link", { button: "left" });
  await waitForMessage((count) => window.__messages.filter((m) => m.type === "link").length >= count, 2);
  const links = (await messages()).filter((m) => m.type === "link");
  check("right-click on a link posts nothing", links.length === 2 && !links.some((m) => m.button === 2), links);
  check("middle then left click post buttons 1 and 0", links.length === 2 && links[0].button === 1 && links[1].button === 0 && links[1].href === "other.md", links);
  // Core refuses a file: link with a host and one outside the group root; the page posts both as written.
  await clearMessages();
  await page.click("#host-link");
  await page.click("#outside-link");
  await waitForMessage((count) => window.__messages.filter((m) => m.type === "link").length >= count, 2);
  const fileLinks = (await messages()).filter((m) => m.type === "link").map((m) => m.href);
  check("file://host and outside-root links are posted as written", fileLinks.join(" ") === "file://host/share/doc.md ../../../outside.md", fileLinks);

  // Steps 7 and 11: find works on the original text and skips diagram and math sources.
  const original = await page.evaluate(() => document.getElementById("turkish").textContent);
  const found = await page.evaluate(() => {
    const result = window.yamv.find("the", "first");
    const marks = Array.from(document.querySelectorAll("mark.yamv-find"));
    return {
      result,
      texts: marks.map((mark) => mark.textContent),
      inSources: marks.filter((mark) => mark.closest("pre.mermaid, .math")).length,
    };
  });
  check("find matches exactly 'the' three times next to İ", found.result.total === 3 && found.texts.every((text) => text.toLowerCase() === "the"), found);
  check("find skips unrendered mermaid and math sources", found.inSources === 0, found);
  const restored = await page.evaluate(() => {
    window.yamv.clearFind();
    const element = document.getElementById("turkish");
    return { text: element.textContent, nodes: element.childNodes.length };
  });
  check("clearFind restores the text exactly", restored.text === original && restored.nodes === 1, restored);

  // Step 11: the render's fragment arrives decoded (by the host's LinkPolicy), and scroll messages carry the generation.
  await clearMessages();
  await render({ ...baseOptions, generation: 8, fragment: "café" });
  const scrolled = await page.evaluate(() => window.scrollY);
  check("a decoded fragment scrolls to its element", scrolled > 0, scrolled);
  // The render's own report arrives once it has settled, so the scroll below is the only source of a report after it.
  await waitForMessage((generation) => window.__messages.some((m) => m.type === "scroll" && m.generation === generation), 8);
  await page.evaluate(() => window.scrollTo(0, 10));
  await waitForMessage((count) => window.__messages.filter((m) => m.type === "scroll").length > count, 1);
  const scrolls = (await messages()).filter((m) => m.type === "scroll");
  check("scroll messages carry the render generation", scrolls.length > 0 && scrolls.every((m) => m.generation === 8), scrolls);

  // Step 8: shortcut capture.
  await page.evaluate(() => {
    window.__keys = [];
    document.addEventListener("keydown", (event) => {
      window.__keys.push({ key: event.key, prevented: event.defaultPrevented, altGraph: event.getModifierState("AltGraph") });
    });
  });
  const press = async (keys) => {
    await clearMessages();
    await page.evaluate(() => { window.__keys = []; });
    await keys();
    return {
      forwarded: (await messages()).filter((m) => m.type === "key"),
      seen: await page.evaluate(() => window.__keys),
    };
  };
  const pressKey = (combo) => press(() => page.keyboard.press(combo));
  let keys = await pressKey("Control+o");
  check("Ctrl+O is forwarded and blocked", keys.forwarded.length === 1 && keys.forwarded[0].key === "o" && keys.seen.at(-1).prevented, keys);
  for (const combo of ["Control+Home", "Control+End", "Control+ArrowDown", "Shift+ArrowLeft", "Control+Backspace", "Control+Delete", "Control+PageDown"]) {
    keys = await pressKey(combo);
    check(`${combo} is neither forwarded nor blocked`, keys.forwarded.length === 0 && !keys.seen.at(-1).prevented, keys);
  }
  keys = await pressKey("Control+;");
  check("Ctrl+; is neither forwarded nor blocked", keys.forwarded.length === 0 && !keys.seen.at(-1).prevented, keys);
  keys = await pressKey("Control+,");
  check("Ctrl+, is forwarded and blocked", keys.forwarded.length === 1 && keys.forwarded[0].key === "," && keys.seen.at(-1).prevented, keys);
  keys = await pressKey("Control+.");
  check("Ctrl+. is neither forwarded nor blocked", keys.forwarded.length === 0 && !keys.seen.at(-1).prevented, keys);
  keys = await pressKey("Control+[");
  check("Ctrl+[ is forwarded", keys.forwarded.length === 1 && keys.forwarded[0].key === "[", keys);
  keys = await pressKey("Control+Tab");
  check("Ctrl+Tab is forwarded", keys.forwarded.length === 1 && keys.forwarded[0].key === "Tab", keys);
  keys = await pressKey("F5");
  check("F5 is forwarded", keys.forwarded.length === 1 && keys.forwarded[0].key === "F5", keys);
  keys = await press(() => page.evaluate(() => {
    const init = { key: "e", ctrlKey: true, altKey: true, modifierAltGraph: true, bubbles: true, cancelable: true };
    document.body.dispatchEvent(new KeyboardEvent("keydown", init));
  }));
  check("AltGr+E is neither forwarded nor blocked", keys.seen.at(-1)?.altGraph === true && keys.forwarded.length === 0 && !keys.seen.at(-1).prevented, keys);

  // A non-Latin layout reports its own letter in `key`; the physical key's Latin letter is `code`.
  const pressLayout = (init) => press(() => page.evaluate((settings) => {
    document.body.dispatchEvent(new KeyboardEvent("keydown", settings));
  }, { bubbles: true, cancelable: true, ...init }));
  keys = await pressLayout({ key: "ς", code: "KeyW", ctrlKey: true });
  check("Ctrl+W on a Greek layout is forwarded as w", keys.forwarded.length === 1 && keys.forwarded[0].key === "w" && keys.seen.at(-1).prevented, keys);
  keys = await pressLayout({ key: "щ", code: "KeyO", ctrlKey: true });
  check("Ctrl+O on a Cyrillic layout is forwarded as o", keys.forwarded.length === 1 && keys.forwarded[0].key === "o" && keys.seen.at(-1).prevented, keys);
  keys = await pressLayout({ key: "ψ", code: "KeyC", ctrlKey: true });
  check("Ctrl+C on a Greek layout is left to the page", keys.forwarded.length === 0 && !keys.seen.at(-1).prevented, keys);
  keys = await pressLayout({ key: "φ", code: "KeyF", ctrlKey: true });
  const findBarShown = await page.evaluate(() => !document.getElementById("yamv-findbar").hidden);
  check("Ctrl+F on a Greek layout opens the find bar", keys.forwarded.length === 0 && keys.seen.at(-1).prevented && findBarShown, { ...keys, findBarShown });
  await page.keyboard.press("Escape");
  keys = await pressLayout({ key: "ě", code: "Digit2", ctrlKey: true });
  check("A letter on a digit key is forwarded unchanged", keys.forwarded.length === 1 && keys.forwarded[0].key === "ě", keys);
  keys = await pressLayout({ key: "ς", code: "KeyW", ctrlKey: false });
  check("Without Ctrl a Greek letter is neither forwarded nor blocked", keys.forwarded.length === 0 && !keys.seen.at(-1).prevented, keys);

  // The bracket shortcuts are the physical keys in the US bracket positions, whatever the layout prints there,
  // with Ctrl (Cmd) and no other modifier.
  keys = await pressLayout({ key: "ü", code: "BracketLeft", ctrlKey: true });
  check(
    "Ctrl+[ on a German layout is forwarded as [",
    keys.forwarded.length === 1 && keys.forwarded[0].key === "[" && keys.forwarded[0].ctrl === true && keys.seen.at(-1).prevented,
    keys,
  );
  keys = await pressLayout({ key: "+", code: "BracketRight", ctrlKey: true });
  check(
    "Ctrl+] on a German layout is forwarded as ]",
    keys.forwarded.length === 1 && keys.forwarded[0].key === "]" && keys.forwarded[0].ctrl === true && keys.seen.at(-1).prevented,
    keys,
  );
  keys = await pressLayout({ key: "[", code: "BracketLeft", ctrlKey: true });
  check("Ctrl+[ on a US layout is forwarded as [", keys.forwarded.length === 1 && keys.forwarded[0].key === "[", keys);
  keys = await pressLayout({ key: "+", code: "BracketRight", metaKey: true });
  check(
    "Cmd+] on a German layout is forwarded as ]",
    keys.forwarded.length === 1 && keys.forwarded[0].key === "]" && keys.forwarded[0].meta === true,
    keys,
  );
  keys = await pressLayout({ key: "ü", code: "BracketLeft", ctrlKey: true, altKey: true, modifierAltGraph: true });
  check(
    "AltGr+[ is neither forwarded nor blocked",
    keys.seen.at(-1)?.altGraph === true && keys.forwarded.length === 0 && !keys.seen.at(-1).prevented,
    keys,
  );
  keys = await pressLayout({ key: "{", code: "BracketLeft", ctrlKey: true, shiftKey: true });
  check("Ctrl+Shift+[ is neither forwarded nor blocked", keys.forwarded.length === 0 && !keys.seen.at(-1).prevented, keys);
  keys = await pressLayout({ key: "[", code: "Minus", ctrlKey: true });
  check("A [ typed on another physical key is neither forwarded nor blocked", keys.forwarded.length === 0 && !keys.seen.at(-1).prevented, keys);
  keys = await pressLayout({ key: "[", code: "BracketLeft", ctrlKey: true, altKey: true });
  check("Ctrl+Alt+[ without AltGr is neither forwarded nor blocked", keys.forwarded.length === 0 && !keys.seen.at(-1).prevented, keys);

  // A confirm card up over the page holds Escape and Enter, wherever focus sits, and posts each as a key. The page stops
  // these two where it sees them, before the find field's own handler, so they are recorded at the window, where a
  // listener added now runs after the page's and sees the page's preventDefault.
  await page.evaluate(() => {
    window.__cardKeys = [];
    window.addEventListener("keydown", (event) => {
      window.__cardKeys.push({ key: event.key, prevented: event.defaultPrevented });
    }, true);
  });
  const pressCardKey = async (combo) => {
    await clearMessages();
    await page.evaluate(() => { window.__cardKeys = []; });
    if (combo.startsWith("AltGraph+")) {
      // Playwright cannot press AltGr, so the page gets the event it sends for it, as the checks above do.
      await page.evaluate((key) => {
        document.body.dispatchEvent(new KeyboardEvent("keydown", { key, modifierAltGraph: true, bubbles: true, cancelable: true }));
      }, combo.slice("AltGraph+".length));
    } else {
      await page.keyboard.press(combo);
    }
    return {
      forwarded: (await messages()).filter((message) => message.type === "key"),
      seen: await page.evaluate(() => window.__cardKeys),
    };
  };
  await page.evaluate(() => window.yamv.setCardOpen(true));
  for (const combo of ["Escape", "Enter", "Control+Enter", "Shift+Escape", "AltGraph+Enter"]) {
    const cardKey = combo.split("+").at(-1);
    keys = await pressCardKey(combo);
    check(
      `${combo} with the card up is forwarded once and blocked`,
      keys.forwarded.length === 1
        && keys.forwarded[0].key === cardKey
        && keys.forwarded[0].ctrl === combo.startsWith("Control+")
        && keys.seen.at(-1).prevented,
      keys,
    );
  }
  await page.evaluate(() => {
    window.yamv.setCardOpen(false);
    document.getElementById("link").focus();
  });
  keys = await pressCardKey("Enter");
  const openLinks = (await messages()).filter((message) => message.type === "link");
  check("Enter on a focused link with no card up posts one link", openLinks.length === 1 && openLinks[0].href === "other.md", { keys, openLinks });
  await page.evaluate(() => window.yamv.setCardOpen(true));
  keys = await pressCardKey("Enter");
  const cardLinks = (await messages()).filter((message) => message.type === "link");
  check("Enter on a focused link with the card up posts no link", cardLinks.length === 0 && keys.forwarded.length === 1, { keys, cardLinks });
  await page.evaluate(() => window.yamv.openFind());
  keys = await pressCardKey("Escape");
  const findBarStaysOpen = await page.evaluate(() => !document.getElementById("yamv-findbar").hidden);
  check(
    "Escape with the find bar open and the card up is forwarded and leaves the bar open",
    keys.forwarded.length === 1 && keys.forwarded[0].key === "Escape" && findBarStaysOpen,
    { keys, findBarStaysOpen },
  );
  await page.evaluate(() => {
    window.yamv.setCardOpen(false);
    window.yamv.clearFind();
    document.getElementById("yamv-findbar").hidden = true;
  });

  // The content width is the text measure: the column's box adds its 44 px side paddings.
  await page.setViewportSize({ width: 1200, height: 800 });
  const widthOf = () => page.evaluate(() => {
    const element = document.getElementById("yamv-content");
    const style = getComputedStyle(element);
    const padding = parseFloat(style.paddingLeft) + parseFloat(style.paddingRight);
    return {
      content: element.clientWidth - padding,
      outer: element.getBoundingClientRect().width,
      viewport: document.documentElement.clientWidth,
    };
  });
  await page.evaluate(() => document.documentElement.style.removeProperty("--yamv-content-width"));
  let width = await widthOf();
  check("With no width set the text measure is 800 px", width.content === 800 && width.outer === 888, width);
  await page.evaluate(() => window.yamv.setContentWidth("760px"));
  width = await widthOf();
  check("760 px sets a 760 px text measure in an 848 px column", width.content === 760 && width.outer === 848, width);
  await page.evaluate(() => window.yamv.setContentWidth("none"));
  width = await widthOf();
  check("none spans the viewport width", width.outer === width.viewport && width.content === width.viewport - 88, width);

  // A render carries its document's width and applies it only once the outgoing document is hidden.
  await render({ ...baseOptions, generation: 20, contentWidth: "600px" });
  width = await widthOf();
  check("a render at 600px shows its document at a 600 px text measure", width.content === 600, width);
  // Mutation records keep the order the changes were made in, so replaying them gives the content's class at each
  // width change, which an observer callback (run after the render's synchronous part) cannot see.
  const widthChanges = await page.evaluate(async ([html, options]) => {
    const root = document.documentElement;
    const content = document.getElementById("yamv-content");
    const records = [];
    const observer = new MutationObserver((list) => records.push(...list));
    observer.observe(root, { attributes: true, attributeFilter: ["style"], attributeOldValue: true });
    observer.observe(content, { attributes: true, attributeFilter: ["class"], attributeOldValue: true });
    await window.yamv.render(html, options);
    records.push(...observer.takeRecords());
    observer.disconnect();
    const valueAfter = (index) => {
      const next = records.slice(index + 1).find((record) => record.target === records[index].target);
      return (next ? next.oldValue : records[index].target.getAttribute(records[index].attributeName)) || "";
    };
    const firstClass = records.find((record) => record.target === content);
    let contentClass = firstClass ? firstClass.oldValue || "" : content.className;
    const seen = [];
    records.forEach((record, index) => {
      if (record.target === content) {
        contentClass = valueAfter(index);
        return;
      }
      const style = document.createElement("div").style;
      style.cssText = valueAfter(index);
      seen.push({
        width: style.getPropertyValue("--yamv-content-width"),
        hidden: contentClass.split(/\s+/).includes("yamv-pending"),
      });
    });
    return seen;
  }, [fixture, { ...baseOptions, generation: 21, contentWidth: "1000px" }]);
  check(
    "the width changes from 600px to 1000px only while the outgoing document is hidden",
    widthChanges.length > 0 && widthChanges.every((change) => change.hidden) && widthChanges.at(-1).width === "1000px",
    widthChanges,
  );
  width = await widthOf();
  check("the 1000px document shows at its width", width.content === 1000, width);

  // The scroll anchor: Core tags each top-level block with the zero-based source line it starts on, one blank line
  // between blocks, so a block's data-line moves by the number of source lines inserted above it.
  await page.setViewportSize({ width: 900, height: 400 });
  const blocksHtml = (count, firstLine, label) =>
    Array.from({ length: count }, (_, index) => `<p data-line="${firstLine + 2 * index}">${label} ${index} text.</p>`).join("\n");
  const longDocument = blocksHtml(80, 0, "Block");
  const anchorOptions = { ...baseOptions, contentWidth: "760px", documentPath: "C:\\docs\\anchor.md" };
  const show = (html, options) => page.evaluate(([h, o]) => window.yamv.render(h, o), [html, options]);
  const topBlockOf = () => page.evaluate(() => {
    const block = Array.from(document.querySelectorAll("#yamv-content [data-line]")).find((element) => element.getBoundingClientRect().bottom > 0);
    return block ? { text: block.textContent, top: Math.round(block.getBoundingClientRect().top), scrollY: window.scrollY } : null;
  });
  const scrollToBlock = (text) => page.evaluate((t) => {
    const block = Array.from(document.querySelectorAll("#yamv-content [data-line]")).find((element) => element.textContent === t);
    window.scrollTo(0, block.getBoundingClientRect().top + window.scrollY);
  }, text);

  await show(longDocument, { ...anchorOptions, generation: 30 });
  // The render's own report, so the scroll's report below is the next one and cannot land after the clear; then the
  // view's own settle, since the resize above and the render both reach the scroll a frame or two later.
  await waitForMessage((generation) => window.__messages.some((m) => m.type === "scroll" && m.generation === generation), 30);
  await waitForScrollSettled();
  await clearMessages();
  await scrollToBlock("Block 30 text.");
  await waitForMessage((count) => window.__messages.filter((m) => m.type === "scroll").length > count, 0);
  const anchorScrolls = (await messages()).filter((m) => m.type === "scroll");
  check("a scroll message carries the top block's source line", anchorScrolls.length > 0 && anchorScrolls.at(-1).line === 60, anchorScrolls);

  const lastScroll = async () => (await messages()).filter((m) => m.type === "scroll").at(-1) ?? null;
  const inserted = `${blocksHtml(10, 0, "Inserted")}\n${blocksHtml(80, 20, "Block")}`;
  await clearMessages();
  await show(inserted, { ...anchorOptions, generation: 31, scroll: 0.1, anchorLine: 0 });
  let top = await topBlockOf();
  check("a reload after 20 lines inserted above the top block stays on that block", top?.text === "Block 30 text." && Math.abs(top.top) <= 1, top);
  await waitForMessage((generation) => window.__messages.some((m) => m.type === "scroll" && m.generation === generation), 31);
  let reported = await lastScroll();
  check("once a reload has landed the page reports its new top line", reported?.line === 80 && reported.generation === 31, reported);

  const shorter = blocksHtml(25, 0, "Short");
  await show(shorter, { ...anchorOptions, generation: 32, scroll: 0.5, anchorLine: 80 });
  const fractionLanding = await page.evaluate(() => ({
    scrollY: window.scrollY,
    expected: 0.5 * Math.max(0, document.documentElement.scrollHeight - window.innerHeight),
  }));
  check(
    "a reload of a shorter document with the anchor past its end falls back to the fraction",
    fractionLanding.expected > 0 && Math.abs(fractionLanding.scrollY - fractionLanding.expected) <= 2,
    fractionLanding,
  );

  await show(longDocument, { ...anchorOptions, documentPath: "C:\\docs\\other.md", generation: 33, scroll: 0, anchorLine: 62 });
  top = await topBlockOf();
  check("a first show with a stored anchor scrolls to that block", top?.text === "Block 31 text." && Math.abs(top.top) <= 1, top);

  // Find keeps its query and the reader's match across a reload.
  const needles = [
    "needle one.", "needle two.", "needle three-a and needle three-b.", "needle four.", "needle five.", "needle six.",
    "needle seven.", "needle eight.",
  ];
  const findDocument = (sentences) => sentences.map((sentence, index) => `<p data-line="${2 * index}">${sentence}</p>`).join("\n");
  const findOptions = { ...anchorOptions, documentPath: "C:\\docs\\find.md" };
  const currentMatch = () => page.evaluate(() => {
    const mark = document.querySelector("#yamv-content mark.yamv-find-current");
    return {
      count: document.getElementById("yamv-find-count").textContent,
      after: mark && mark.nextSibling ? mark.nextSibling.textContent : null,
    };
  });
  await show(findDocument(needles), { ...findOptions, generation: 34 });
  await page.evaluate(() => {
    window.yamv.openFind();
    window.yamv.find("needle", "first");
    for (let step = 0; step < 3; step++) {
      window.yamv.find("needle", "next");
    }
  });
  let match = await currentMatch();
  check("find selects the 4th of 9 matches before the reload", match.count === "4 of 9" && match.after === " three-b.", match);
  const withInsert = ["needle zero.", ...needles];
  await show(findDocument(withInsert), { ...findOptions, generation: 35 });
  match = await currentMatch();
  check(
    "a reload with one match inserted above keeps the query and the same sentence",
    match.count === "5 of 10" && match.after === " three-b.",
    match,
  );
  await show(findDocument(withInsert.filter((sentence) => !sentence.startsWith("needle three-a"))), { ...findOptions, generation: 36 });
  match = await currentMatch();
  check("a reload that deletes the selected match's block selects the next match below", match.count === "4 of 8" && match.after === " four.", match);
  await page.evaluate(() => document.getElementById("yamv-find-close").click());

  // Mutation records keep the order the changes were made in: replaying them gives the content's class and child count
  // after each change, which a frame sample can miss. The observer's first callback runs right after the swap, before
  // anything the render awaits, so it sees what the swap brought in. The page shows find.md at 760px, from generation 36.
  const laneB = await page.evaluate(async ([html, options]) => {
    const root = document.documentElement;
    const content = document.getElementById("yamv-content");
    const frames = [];
    let sampling = true;
    const sample = () => {
      frames.push({ children: content.childElementCount, pending: content.classList.contains("yamv-pending") });
      if (sampling) {
        window.requestAnimationFrame(sample);
      }
    };
    const records = [];
    let atSwap = null;
    const seeSwap = (list) => {
      if (!atSwap && list.some((record) => record.type === "childList")) {
        atSwap = { math: content.querySelector(".katex") !== null, code: content.querySelector(".hljs-keyword") !== null };
      }
      records.push(...list);
    };
    const observer = new MutationObserver(seeSwap);
    observer.observe(content, { childList: true, attributes: true, attributeFilter: ["class"], attributeOldValue: true });
    observer.observe(root, { attributes: true, attributeFilter: ["style"] });
    window.requestAnimationFrame(sample);
    await window.yamv.render(html, options);
    sampling = false;
    seeSwap(observer.takeRecords());
    observer.disconnect();
    const classRecords = records.filter((record) => record.attributeName === "class");
    const classValues = classRecords.map((record, index) =>
      (index + 1 < classRecords.length ? classRecords[index + 1].oldValue : content.className) || "");
    const swap = records.findIndex((record) => record.type === "childList");
    const widthChanges = records.map((record, index) => (record.attributeName === "style" ? index : -1)).filter((index) => index >= 0);
    return {
      frames: frames.length,
      emptied: records.filter((record) => record.type === "childList" && record.addedNodes.length === 0).length,
      hiddenChanges: classValues.filter((value) => value.split(/\s+/).includes("yamv-pending")).length,
      hiddenFrames: frames.filter((frame) => frame.pending).length,
      emptyFrames: frames.filter((frame) => frame.children === 0).length,
      replaced: swap >= 0,
      atSwap,
      widthChangesBeforeSwap: widthChanges.filter((index) => index < swap).length,
      widthChangesAfterSwap: widthChanges.filter((index) => index > swap).length,
      width: root.style.getPropertyValue("--yamv-content-width"),
    };
  }, [
    `${longDocument}\n<p data-line="200"><span class="math">\\(x^2\\)</span></p>\n` +
      `<pre><code class="language-csharp" data-line="202">public class Widget { }</code></pre>`,
    { ...findOptions, generation: 37, hasMath: true, contentWidth: "700px" },
  ]);
  check(
    "a same-document render never shows an empty or hidden content element",
    laneB.replaced && laneB.emptied === 0 && laneB.hiddenChanges === 0 && laneB.hiddenFrames === 0 && laneB.emptyFrames === 0,
    laneB,
  );
  check("a same-document render swaps in its math and code already rendered", laneB.atSwap?.math === true && laneB.atSwap.code === true, laneB);
  check(
    "a same-document render changes the width only once its new content is in",
    laneB.widthChangesBeforeSwap === 0 && laneB.widthChangesAfterSwap > 0 && laneB.width === "700px",
    laneB,
  );

  // A width the host sends while a reload is staged is the width the reload lands at, not the one its render carried.
  const lateWidth = await page.evaluate(async ([html, options]) => {
    const rendering = window.yamv.render(html, options);
    window.yamv.setContentWidth("650px");
    await rendering;
    const element = document.getElementById("yamv-content");
    const style = getComputedStyle(element);
    return {
      variable: document.documentElement.style.getPropertyValue("--yamv-content-width"),
      content: element.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight),
    };
  }, [longDocument, { ...findOptions, generation: 38, contentWidth: "700px" }]);
  check("a width change during a staged reload is the width it lands at", lateWidth.variable === "650px" && lateWidth.content === 650, lateWidth);

  // A show of the first block from its top lands at the very top, with the page's top padding above it.
  await show(longDocument, { ...anchorOptions, documentPath: "C:\\docs\\first.md", generation: 39, scroll: 0.5, anchorLine: 0, anchorOffset: 0 });
  const firstScroll = await page.evaluate(() => window.scrollY);
  check("a show anchored on the first block from its top lands at scrollY 0", firstScroll === 0, firstScroll);

  // The offset within a tall block: a reader halfway down a 300-item list (one top-level block) comes back to the same item
  // after a reload, and after switching away and back with the anchor the page reported.
  const items = Array.from({ length: 300 }, (_, index) => `<li>Item ${index}</li>`).join("");
  const listDocument = (above) =>
    `${above ? '<p data-line="0">Added above.</p>\n' : ""}<ul data-line="${above ? 2 : 0}">${items}</ul>\n` +
    `<p data-line="${above ? 303 : 301}">After.</p>`;
  const listOptions = { ...anchorOptions, documentPath: "C:\\docs\\list.md" };
  const topItem = () => page.evaluate(() => {
    const item = Array.from(document.querySelectorAll("#yamv-content li")).find((element) => element.getBoundingClientRect().bottom > 0);
    return item ? { text: item.textContent, top: Math.round(item.getBoundingClientRect().top) } : null;
  });
  await show(listDocument(false), { ...listOptions, generation: 40 });
  // The render's own report first, so the scroll below is the only report after it and its count is exact.
  await waitForMessage((generation) => window.__messages.some((m) => m.type === "scroll" && m.generation === generation), 40);
  await page.evaluate(() => {
    const item = Array.from(document.querySelectorAll("#yamv-content li")).find((element) => element.textContent === "Item 150");
    window.scrollTo(0, item.getBoundingClientRect().top + window.scrollY);
  });
  await waitForMessage((count) => window.__messages.filter((m) => m.type === "scroll").length > count, 1);
  await clearMessages();
  await show(listDocument(true), { ...listOptions, generation: 41 });
  let item = await topItem();
  check("a reload keeps the reader on the same item halfway down a long list", item?.text === "Item 150" && Math.abs(item.top) <= 1, item);
  await waitForMessage((generation) => window.__messages.some((m) => m.type === "scroll" && m.generation === generation), 41);
  reported = await lastScroll();
  check(
    "the scroll report carries the list's line and the offset within it",
    reported?.line === 2 && reported.offset > 0.4 && reported.offset < 0.6,
    reported,
  );
  await show(shorter, { ...anchorOptions, documentPath: "C:\\docs\\elsewhere.md", generation: 42 });
  await show(listDocument(true), { ...listOptions, generation: 43, scroll: 0, anchorLine: reported?.line, anchorOffset: reported?.offset });
  item = await topItem();
  check("switching back with the reported anchor lands on the same item", item?.text === "Item 150" && Math.abs(item.top) <= 1, item);

  // Pins: a view at the very bottom follows text appended to the document; a view at the very top stays there.
  const pinOptions = { ...anchorOptions, documentPath: "C:\\docs\\pins.md" };
  const viewEdges = () => page.evaluate(() => ({
    scrollY: window.scrollY,
    gap: document.documentElement.scrollHeight - (window.scrollY + window.innerHeight),
  }));
  await show(longDocument, { ...pinOptions, generation: 44 });
  await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
  await show(blocksHtml(90, 0, "Block"), { ...pinOptions, generation: 45 });
  let edges = await viewEdges();
  check("a view at the very bottom stays at the bottom when text is appended", edges.gap <= 2 && edges.scrollY > 0, edges);
  await page.evaluate(() => window.scrollTo(0, 0));
  await show(`<p data-line="0">New first block.</p>\n${blocksHtml(90, 2, "Block")}`, { ...pinOptions, generation: 46 });
  edges = await viewEdges();
  check("a view at the very top stays at the top when a block is inserted above", edges.scrollY === 0, edges);

  // Raw HTML with a data-line of its own is no anchor, and its malformed line never reaches the scroll message.
  await clearMessages();
  const rawDocument = `<div data-line="1.5">Raw block.</div>\n${blocksHtml(40, 2, "Block")}`;
  await show(rawDocument, { ...anchorOptions, documentPath: "C:\\docs\\raw.md", generation: 47 });
  await waitForMessage((generation) => window.__messages.some((m) => m.type === "scroll" && m.generation === generation), 47);
  reported = await lastScroll();
  check("a raw data-line is skipped: the scroll report names the first Core block", reported?.line === 2 && reported.generation === 47, reported);

  // Reselecting the find match after a reload never scrolls: the reader scrolled past the selected match stays put.
  const farDocument = longDocument.replace("Block 5 text.", "Block 5 needle text.");
  const farOptions = { ...anchorOptions, documentPath: "C:\\docs\\far.md" };
  await show(farDocument, { ...farOptions, generation: 48 });
  await page.evaluate(() => {
    window.yamv.openFind();
    window.yamv.find("needle", "first");
  });
  await scrollToBlock("Block 40 text.");
  await show(farDocument, { ...farOptions, generation: 49 });
  top = await topBlockOf();
  match = await currentMatch();
  check(
    "a reload with find open keeps the view on the anchor, not on the selected match",
    top?.text === "Block 40 text." && Math.abs(top.top) <= 1 && match.count === "1 of 1",
    { top, match },
  );

  // A selected match whose block changed text is found by its block's line: the same order, else the block's last match;
  // with no match in that block, the first below, else the last match.
  const typoOptions = { ...anchorOptions, documentPath: "C:\\docs\\typo.md" };
  const typoDocument = (paragraph) => `<p data-line="0">Intro.</p>\n<p data-line="2">${paragraph}</p>\n<p data-line="4">Plain end.</p>`;
  await show(typoDocument("needle a, needle b, needle c, needle d, teh end."), { ...typoOptions, generation: 50 });
  await page.evaluate(() => {
    window.yamv.find("needle", "first");
    window.yamv.find("needle", "next");
    window.yamv.find("needle", "next");
  });
  await show(typoDocument("needle a, needle b, needle c, needle d, the end."), { ...typoOptions, generation: 51 });
  match = await currentMatch();
  check("a typo fixed elsewhere in the paragraph keeps its 3rd match selected", match.count === "3 of 4" && match.after === " c, ", match);
  await show(typoDocument("needle a, needle b, the end."), { ...typoOptions, generation: 52 });
  match = await currentMatch();
  check("a block left with fewer matches selects its last", match.count === "2 of 2" && match.after === " b, the end.", match);
  await show(`<p data-line="0">needle one, needle two.</p>\n<p data-line="2">Plain.</p>`, { ...typoOptions, generation: 53 });
  match = await currentMatch();
  check("with no match in the block and none below the last match is selected", match.count === "2 of 2" && match.after === " two.", match);
  await page.evaluate(() => document.getElementById("yamv-find-close").click());

  // A diagram block is matched by its source, not the svg text it renders to.
  const diagram = '<pre class="mermaid" data-line="40">graph TD; A --> B</pre>';
  const diagramOptions = { ...anchorOptions, documentPath: "C:\\docs\\diagram.md", hasMermaid: true };
  const diagramDocument = `${blocksHtml(20, 0, "Para")}\n${diagram}\n${blocksHtml(30, 42, "Tail")}`;
  const diagramMoved =
    `${blocksHtml(10, 0, "Inserted")}\n${blocksHtml(20, 20, "Para")}\n${diagram.replace("40", "60")}\n${blocksHtml(30, 62, "Tail")}`;
  const diagramTop = () => page.evaluate(() => Math.round(document.querySelector("#yamv-content pre.mermaid").getBoundingClientRect().top));
  await show(diagramDocument, { ...diagramOptions, generation: 54 });
  await page.evaluate(() => {
    const block = document.querySelector("#yamv-content pre.mermaid");
    window.scrollTo(0, block.getBoundingClientRect().top + window.scrollY + 5);
  });
  await show(diagramMoved, { ...diagramOptions, generation: 55 });
  const movedTop = await diagramTop();
  check("a reload with 20 lines inserted above keeps a diagram block at the top", Math.abs(movedTop + 5) <= 1, movedTop);

  // A theme change while a reload is staged reaches the staged diagrams: they land drawn in the new theme.
  const themed = await page.evaluate(async ([html, options]) => {
    const rendering = window.yamv.render(html, options);
    const staged = document.querySelector(".yamv-content.yamv-pending[aria-hidden='true']");
    let changedWhileStaged = false;
    const observer = new MutationObserver(() => {
      if (!changedWhileStaged && staged.querySelector("svg")) {
        changedWhileStaged = staged.isConnected;
        window.yamv.setTheme(":root { --yamv-code-background: #123456; }");
      }
    });
    observer.observe(staged, { childList: true, subtree: true });
    await rendering;
    observer.disconnect();
    const svg = document.querySelector("#yamv-content pre.mermaid svg");
    return { changedWhileStaged, newTheme: svg !== null && svg.outerHTML.toLowerCase().includes("#123456") };
  }, [diagramMoved, { ...diagramOptions, generation: 56 }]);
  check("a theme change during a staged reload reaches its diagrams", themed.changedWhileStaged && themed.newTheme, themed);
  await page.evaluate(() => window.yamv.setTheme(""));

  // Scroll to a fragment without a render: scrollToId takes the id decoded once (by the host's Core), so an id holding
  // "%25" and a '"' is found as written; a render's fragment arrives decoded and is used as written.
  {
    const hostileId = 'frag%25"id';
    const fragmentHtml = `${blocksHtml(40, 0, "Before")}\n<h2 id="frag%25&quot;id" data-line="80">Target</h2>\n${blocksHtml(40, 82, "After")}`;
    const fragmentOptions = { ...anchorOptions, documentPath: "C:\\docs\\fragment.md" };
    const targetTop = () => page.evaluate((id) => Math.round(document.getElementById(id).getBoundingClientRect().top), hostileId);
    await show(fragmentHtml, { ...fragmentOptions, generation: 60 });
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.waitForFunction(() => window.scrollY === 0);
    await clearMessages();
    const found = await page.evaluate((id) => window.yamv.scrollToId(id), hostileId);
    await page.waitForFunction((id) => Math.abs(document.getElementById(id).getBoundingClientRect().top) <= 1, hostileId);
    check("scrollToId scrolls to an id holding %25 and a quote, decoded once", found === true && Math.abs(await targetTop()) <= 1, await targetTop());
    await clearMessages();
    const missing = await page.evaluate(() => {
      const before = window.scrollY;
      return { found: window.yamv.scrollToId("no-such-id"), moved: window.scrollY !== before };
    });
    // A scroll report the earlier scroll left pending may still arrive; scrollToId itself posts nothing.
    const afterMissing = (await messages()).filter((message) => message.type !== "scroll");
    const missingOk = missing.found === false && !missing.moved && afterMissing.length === 0;
    check("scrollToId of a missing id does nothing and posts nothing", missingOk, { missing, afterMissing });
    // The host passes a render's fragment already decoded (by LinkPolicy); the page decodes it no further.
    const againOptions = { ...fragmentOptions, documentPath: "C:\\docs\\fragment-again.md", fragment: hostileId, generation: 61 };
    await show(fragmentHtml, againOptions);
    await page.waitForFunction((id) => Math.abs(document.getElementById(id).getBoundingClientRect().top) <= 1, hostileId);
    check("a render with the decoded fragment lands on the same element", Math.abs(await targetTop()) <= 1, await targetTop());
    await clearMessages();
    await show(fragmentHtml, { ...fragmentOptions, documentPath: "C:\\docs\\fragment-percent.md", fragment: "100%", generation: 62 });
    const percentErrors = (await messages()).filter((message) => message.type === "error");
    check("a render whose fragment holds a bare % posts no error", percentErrors.length === 0, percentErrors);
  }

  // A double click on a link posts it once: a single click elsewhere afterwards is the sentinel that the clicks are done.
  {
    await show(fixture, { ...baseOptions, generation: 70 });
    await clearMessages();
    await page.dblclick("#link");
    await page.click("#outside-link");
    await page.waitForFunction(() => window.__messages.some((message) => message.type === "link" && message.href === "../../../outside.md"));
    const posted = (await messages()).filter((message) => message.type === "link").map((message) => message.href);
    const sentinel = posted.indexOf("../../../outside.md");
    const beforeSentinel = posted.slice(0, sentinel);
    check("a double click on a link posts it once", beforeSentinel.length === 1 && beforeSentinel[0] === "other.md", posted);
  }

  // A drag over the page tells the host only when it holds files, and never takes the drop from the web view.
  {
    const dragOver = (withFiles) => page.evaluate((files) => {
      const dataTransfer = new DataTransfer();
      if (files) {
        dataTransfer.items.add(new File(["x"], "a.md"));
      } else {
        dataTransfer.setData("text/plain", "words");
      }
      const event = new DragEvent("dragover", { bubbles: true, cancelable: true, dataTransfer });
      document.body.dispatchEvent(event);
      return { types: Array.from(dataTransfer.types), prevented: event.defaultPrevented, posted: window.__messages.filter((m) => m.type === "drag") };
    }, withFiles);
    await clearMessages();
    await page.evaluate(() => document.body.dispatchEvent(new DragEvent("dragstart", { bubbles: true })));
    const inPage = await dragOver(true);
    await page.evaluate(() => document.body.dispatchEvent(new DragEvent("dragend", { bubbles: true })));
    check("a drag begun in the page posts no drag message", inPage.posted.length === 0, inPage);
    const text = await dragOver(false);
    check("a text drag over the page posts no drag message", text.posted.length === 0 && !text.prevented, text);
    const textLeave = await page.evaluate(() => {
      const dataTransfer = new DataTransfer();
      dataTransfer.setData("text/plain", "words");
      document.body.dispatchEvent(new DragEvent("dragleave", { bubbles: true, dataTransfer }));
      return window.__messages.filter((m) => m.type === "drag");
    });
    check("a text drag's dragleave posts nothing", textLeave.length === 0, textLeave);
    const files = await dragOver(true);
    check("a file drag over the page posts drag and is not default-prevented", files.posted.length === 1 && files.posted[0].over === true && !files.prevented, files);
  }

  // Copy as raw: the page sends each selection end as a point Core maps back to the source. The HTML is Core's own
  // (MarkdownRenderer) for this markdown, so the data-yamv-src numbers are real source offsets; each expected pair below maps
  // through RawSelection.Extract to the markdown named beside it.
  //   # Copy as raw\n\nMath $x^2$ then see **the guide** for more\n\nsee **the guide** for more\n\n- one\n- two\n\n
  //   <div class="raw">\n<p>raw text</p>\n</div>\n\nLast paragraph.\n
  {
    const rawHtml = [
      '<h1 id="copy-as-raw" data-line="0" data-yamv-src="0">Copy as raw</h1>',
      '<p data-line="2" data-yamv-src="15">Math <span class="math" data-yamv-src="20">\\(x^2\\)</span> then see '
        + '<strong data-yamv-src="35">the guide</strong> for more</p>',
      '<p data-line="4" data-yamv-src="59">see <strong data-yamv-src="63">the guide</strong> for more</p>',
      '<ul data-line="6" data-yamv-src="87">',
      '<li data-yamv-src="87">one</li>',
      '<li data-yamv-src="93">two</li>',
      "</ul>",
      '<div class="raw">',
      "<p>raw text</p>",
      "</div>",
      '<p data-line="13" data-yamv-src="142">Last paragraph.</p>',
      "",
    ].join("\n");
    await show(rawHtml, { ...baseOptions, documentPath: "C:\\docs\\raw-copy.md", generation: 90, hasMath: true });
    // Selects from one end to the other and asks for Copy as raw. An end is { tag, nth, text, offset }: the text-th text
    // node of the nth element with that data-yamv-src; { raw: true, offset }: the raw block's text; { element: tag, nth, offset }:
    // a boundary in that element; or { before: tag, offset }: the content boundary before that block.
    const selectAndCopy = (ends) => page.evaluate(([from, to]) => {
      window.__messages = [];
      const content = document.getElementById("yamv-content");
      const tagged = (spec) => content.querySelectorAll(`[data-yamv-src="${spec}"]`);
      const textOf = (element, index) => Array.from(element.childNodes).filter((node) => node.nodeType === Node.TEXT_NODE)[index];
      const boundary = (end) => {
        if (end.raw) {
          return [textOf(content.querySelector("div.raw p"), 0), end.offset];
        }
        if (end.before !== undefined) {
          return [content, Array.from(content.childNodes).indexOf(tagged(end.before)[0])];
        }
        if (end.element !== undefined) {
          return [tagged(end.element)[end.nth ?? 0], end.offset];
        }
        return [textOf(tagged(end.tag)[end.nth ?? 0], end.text ?? 0), end.offset];
      };
      const range = document.createRange();
      range.setStart(...boundary(from));
      range.setEnd(...boundary(to));
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
      window.yamv.copyRaw();
      return window.__messages.filter((message) => message.type === "rawSelection");
    }, ends);
    const expectPoints = async (name, ends, start, end) => {
      const posted = await selectAndCopy(ends);
      const ok = posted.length === 1
        && JSON.stringify(posted[0].start) === JSON.stringify(start)
        && JSON.stringify(posted[0].end) === JSON.stringify(end)
        && posted[0].generation === 90;
      check(name, ok, posted);
    };
    const mathRendered = await page.evaluate(() => document.querySelector("#yamv-content .katex") !== null);
    check("the copy-as-raw fixture's math is rendered", mathRendered, mathRendered);
    // "see **the guide**": a start in plain text, an end inside the bold.
    await expectPoints(
      "a selection ending inside a bold word names the bold's own element",
      [{ tag: 59, offset: 0 }, { tag: 63, offset: 3 }],
      { node: 59, after: null, offset: 0 },
      { node: 63, after: null, offset: 3 },
    );
    // "see **the guide** for more": element-boundary ends, as a triple click leaves them, the end past the list's whitespace.
    await expectPoints(
      "element-boundary ends move to the paragraph's first and last text",
      [{ before: 59 }, { element: 87, nth: 1, offset: 0 }],
      { node: 59, after: null, offset: 0 },
      { node: 59, after: 63, offset: 9 },
    );
    // "Math $x^2$ then": the rendered math's text is not counted after it.
    await expectPoints(
      "an end after inline math counts from the math element",
      [{ tag: 15, offset: 0 }, { tag: 15, text: 1, offset: 5 }],
      { node: 15, after: null, offset: 0 },
      { node: 15, after: 20, offset: 5 },
    );
    // The whole raw HTML block: the start is the end of the text before it, the end the start of the text after it.
    await expectPoints(
      "a selection inside a raw HTML block names the text around it",
      [{ raw: true, offset: 2 }, { raw: true, offset: 5 }],
      { node: 93, after: null, offset: 3 },
      { node: 142, after: null, offset: 0 },
    );
    // "- two": an end at the raw block's very first text is the end of the text before it, so the raw block is not copied.
    await expectPoints(
      "an end at the start of a raw HTML block is the end of the text before it",
      [{ tag: 93, offset: 0 }, { raw: true, offset: 0 }],
      { node: 93, after: null, offset: 0 },
      { node: 93, after: null, offset: 3 },
    );
    // "Last": a start at the raw block's very last text is the start of the paragraph after it.
    await expectPoints(
      "a start at the end of a raw HTML block is the start of the block after it",
      [{ raw: true, offset: 8 }, { tag: 142, offset: 4 }],
      { node: 142, after: null, offset: 0 },
      { node: 142, after: null, offset: 4 },
    );
    const nothing = await page.evaluate(() => {
      window.__messages = [];
      window.getSelection().removeAllRanges();
      window.yamv.copyRaw();
      return window.__messages;
    });
    check("copyRaw with no selection posts nothing", nothing.length === 0, nothing);

    await page.evaluate(() => {
      const content = document.getElementById("yamv-content");
      const range = document.createRange();
      range.selectNodeContents(content.querySelector('[data-yamv-src="59"]'));
      window.getSelection().removeAllRanges();
      window.getSelection().addRange(range);
    });
    keys = await pressKey("Control+Shift+C");
    let raw = (await messages()).filter((message) => message.type === "rawSelection");
    check(
      "Ctrl+Shift+C posts a rawSelection, no key, and is blocked",
      raw.length === 1 && keys.forwarded.length === 0 && keys.seen.at(-1).prevented,
      { raw, keys },
    );
    keys = await pressKey("Control+c");
    raw = (await messages()).filter((message) => message.type === "rawSelection");
    check("Ctrl+C posts neither a rawSelection nor a key", raw.length === 0 && keys.forwarded.length === 0, { raw, keys });
  }

  // Copy as raw against Core's own HTML (MarkdownRenderer) for the markdown beside each fixture. Each expected pair was
  // mapped through RawSelection.Extract (a scratch console app over Yamv.Core) to the copy named in its comment.
  {
    const coreHtml = {
      rawcode: [
        "<p data-line=\"0\" data-yamv-src=\"0\">Intro para.</p>",
        "<div class=\"raw\">",
        "<p>raw text</p>",
        "</div>",
        "<pre><code class=\"language-js\" data-line=\"6\" data-yamv-src=\"55\">let x = 1;",
        "</code></pre>",
        "<p data-line=\"10\" data-yamv-src=\"77\">After para.</p>",
        "",
      ].join("\n"),
      codenext: [
        "<p data-line=\"0\" data-yamv-src=\"0\">Intro para.</p>",
        "<pre><code class=\"language-js\" data-line=\"2\" data-yamv-src=\"13\">let x = 1;",
        "</code></pre>",
        "<div class=\"raw\">",
        "<p>raw text</p>",
        "</div>",
        "<p data-line=\"10\" data-yamv-src=\"77\">After para.</p>",
        "",
      ].join("\n"),
      dlnested: [
        "<p data-line=\"0\" data-yamv-src=\"0\">intro</p>",
        "<ul data-line=\"2\" data-yamv-src=\"7\">",
        "<li data-yamv-src=\"7\"><p data-yamv-src=\"9\">one</p>",
        "</li>",
        "<li data-yamv-src=\"13\"><p data-yamv-src=\"15\">two</p>",
        "<dl>",
        "<dt>Term</dt>",
        "<dd>",
        "<p>Definition</p>",
        "</dd>",
        "</dl>",
        "</li>",
        "</ul>",
        "<p data-line=\"8\" data-yamv-src=\"43\">after para</p>",
        "",
      ].join("\n"),
      hrraw: [
        "<p data-line=\"0\" data-yamv-src=\"0\">Para.</p>",
        "<hr data-line=\"2\" data-yamv-src=\"7\" />",
        "<div class=\"raw\">",
        "<p>raw text</p>",
        "</div>",
        "<p data-line=\"8\" data-yamv-src=\"54\">After.</p>",
        "",
      ].join("\n"),
      lazy: [
        "<p data-line=\"0\" data-yamv-src=\"0\">text <img data-src=\"lazy.png\" src=\"x.png\"> more "
          + "<strong data-yamv-src=\"48\">bold</strong> end</p>",
        "",
      ].join("\n"),
      lazy2: [
        "<p data-src=\"1.5\">raw para</p>",
        "<p data-line=\"2\" data-yamv-src=\"32\">next para</p>",
        "",
      ].join("\n"),
      image: [
        "<p data-line=\"0\" data-yamv-src=\"0\">see <img src=\"x.png\" data-yamv-src=\"4\" alt=\"alt\" /></p>",
        "<p data-line=\"2\" data-yamv-src=\"19\"><img src=\"y.png\" data-yamv-src=\"19\" alt=\"solo\" /></p>",
        "<p data-line=\"4\" data-yamv-src=\"35\">next para</p>",
        "",
      ].join("\n"),
      mermaid: [
        "<p data-line=\"0\" data-yamv-src=\"0\">before para</p>",
        "<pre class=\"mermaid\" data-line=\"2\" data-yamv-src=\"13\">graph TD; A-->B",
        "</pre>",
        "<p data-line=\"6\" data-yamv-src=\"45\">after para</p>",
        "",
      ].join("\n"),
      rawonly: [
        "<div class=\"raw\">",
        "<p>raw only</p>",
        "</div>",
        "",
      ].join("\n"),
      find: [
        "<p data-line=\"0\" data-yamv-src=\"0\">see <strong data-yamv-src=\"4\">the guide</strong> for more</p>",
        "",
      ].join("\n"),
      mathold: [
        "<p data-line=\"0\" data-yamv-src=\"0\">before para</p>",
        "<p data-line=\"2\" data-yamv-src=\"13\">after para</p>",
        "",
      ].join("\n"),
      mathnew: [
        "<p data-line=\"0\" data-yamv-src=\"0\">inserted <span class=\"math\" data-yamv-src=\"9\">\\(x\\)</span> line</p>",
        "<p data-line=\"2\" data-yamv-src=\"19\">before para</p>",
        "<p data-line=\"4\" data-yamv-src=\"32\">after para</p>",
        "",
      ].join("\n"),
      tableraw: [
        "<p data-line=\"0\" data-yamv-src=\"0\">Intro.</p>",
        "<table data-line=\"2\" data-yamv-src=\"8\">",
        "<thead>",
        "<tr data-yamv-src=\"9\">",
        "<th data-yamv-src=\"9\">a</th>",
        "<th data-yamv-src=\"13\">b</th>",
        "</tr>",
        "</thead>",
        "<tbody>",
        "<tr data-yamv-src=\"29\">",
        "<td data-yamv-src=\"29\">1</td>",
        "<td data-yamv-src=\"33\">2</td>",
        "</tr>",
        "</tbody>",
        "</table>",
        "<div class=\"raw\">",
        "<p>raw text</p>",
        "</div>",
        "<table data-line=\"10\" data-yamv-src=\"81\">",
        "<thead>",
        "<tr data-yamv-src=\"82\">",
        "<th data-yamv-src=\"82\">c</th>",
        "<th data-yamv-src=\"86\">d</th>",
        "</tr>",
        "</thead>",
        "<tbody>",
        "<tr data-yamv-src=\"102\">",
        "<td data-yamv-src=\"102\">3</td>",
        "<td data-yamv-src=\"106\">4</td>",
        "</tr>",
        "</tbody>",
        "</table>",
        "<p data-line=\"14\" data-yamv-src=\"112\">After.</p>",
        "",
      ].join("\n"),
      rawend: [
        "<p data-line=\"0\" data-yamv-src=\"0\">Para.</p>",
        "<div class=\"raw\">",
        "<p>raw text</p>",
        "</div>",
        "",
      ].join("\n"),
      readmea: [
        "<p align=\"center\">",
        "  <img src=\"logo.png\" alt=\"Logo\">",
        "</p>",
        "<h1 align=\"center\">Project</h1>",
        "<p data-line=\"6\" data-yamv-src=\"92\">Some intro text.</p>",
        "",
      ].join("\n"),
      readmeb: [
        "<p align=\"center\"><img src=\"logo.png\" alt=\"Logo\"></p>",
        "<p align=\"center\"><a href=\"https://x.example\"><img src=\"badge.svg\" alt=\"build\"></a> "
          + "<a href=\"https://y.example\"><img src=\"badge2.svg\" alt=\"cov\"></a></p>",
        "<h1 id=\"project\" data-line=\"4\" data-yamv-src=\"209\">Project</h1>",
        "<p data-line=\"6\" data-yamv-src=\"220\">Some intro text.</p>",
        "",
      ].join("\n"),
      logobottom: [
        "<p data-line=\"0\" data-yamv-src=\"0\">Para.</p>",
        "<p align=\"center\"><img src=\"logo.png\" alt=\"Logo\"></p>",
        "",
      ].join("\n"),
    };
    // An end is { css, nth, text, offset }: the text-th non-blank text node under the nth match of css in the content;
    // without `text`, a boundary in that element; { label, offset }: the text node reading `label` outside style sheets;
    // { content: true, offset }: a boundary in the content itself, -1 for its end.
    const copyIn = (ends) => page.evaluate(([from, to]) => {
      window.__messages = [];
      const content = document.getElementById("yamv-content");
      const textsUnder = (root) => {
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        const texts = [];
        for (let node = walker.nextNode(); node; node = walker.nextNode()) {
          if (node.data.trim() !== "" && !node.parentElement.closest("style, script")) {
            texts.push(node);
          }
        }
        return texts;
      };
      const boundary = (end) => {
        if (end.content) {
          return [content, end.offset < 0 ? content.childNodes.length : end.offset];
        }
        if (end.label !== undefined) {
          return [textsUnder(content).find((text) => text.data.trim() === end.label), end.offset];
        }
        const element = content.querySelectorAll(end.css)[end.nth ?? 0];
        return end.text === undefined ? [element, end.offset] : [textsUnder(element)[end.text], end.offset];
      };
      const range = document.createRange();
      range.setStart(...boundary(from));
      range.setEnd(...boundary(to));
      window.getSelection().removeAllRanges();
      window.getSelection().addRange(range);
      window.yamv.copyRaw();
      return window.__messages.filter((message) => message.type === "rawSelection");
    }, ends);
    let generation = 200;
    const showCore = async (name, extra = {}) => {
      generation++;
      await show(coreHtml[name], { ...baseOptions, documentPath: `C:\\docs\\${name}.md`, generation, ...extra });
    };
    const expectCopy = async (name, ends, start, end) => {
      const posted = await copyIn(ends);
      const ok = posted.length === 1
        && JSON.stringify(posted[0].start) === JSON.stringify(start)
        && JSON.stringify(posted[0].end) === JSON.stringify(end)
        && posted[0].generation === generation;
      check(name, ok, posted);
    };
    const point = (node, after, offset) => ({ node, after, offset });
    const raw = { css: "div.raw p", text: 0 };
    const top = (nth, text) => ({ css: ":scope > p", nth, text });

    // Intro, raw block, code block. "<div class=\"raw\">\n<p>raw text</p>\n</div>": the raw block alone.
    await showCore("rawcode");
    await expectCopy(
      "a selection inside a raw block before a code block copies the raw block alone",
      [{ ...raw, offset: 2 }, { ...raw, offset: 5 }],
      point(0, null, 11),
      point(55, null, 0),
    );
    // Intro, code block, raw block, After. "<div class=\"raw\">\n<p>raw text</p>\n</div>\n\nAfter": no code block.
    await showCore("codenext");
    await expectCopy(
      "a selection from inside a raw block after a code block copies no code block",
      [{ ...raw, offset: 2 }, { ...top(1, 0), offset: 5 }],
      point(13, null, 11),
      point(77, null, 5),
    );
    // "```js\nlet x = 1;\n```": an end at the raw block's first text ends with the code block before it.
    await expectCopy(
      "an end at a raw block's first text after a code block copies the code block",
      [{ css: "code", text: 0, offset: 0 }, { ...raw, offset: 0 }],
      point(13, null, 0),
      point(13, null, 11),
    );
    // A definition list inside the second list item. "- two".
    await showCore("dlnested");
    await expectCopy(
      "an end at a nested definition list's first text ends with the text before it",
      [{ css: "li p", nth: 1, text: 0, offset: 0 }, { css: "dt", text: 0, offset: 0 }],
      point(15, null, 0),
      point(15, null, 3),
    );
    // Para, a rule, a raw block. "Para.\n\n---": the rule before the raw block is a position, named by its own tag.
    await showCore("hrraw");
    await expectCopy(
      "an end at a raw block's first text after a rule keeps the rule",
      [{ ...top(0, 0), offset: 0 }, { ...raw, offset: 0 }],
      point(0, null, 0),
      point(7, null, 1),
    );
    // text <img data-src="lazy.png" src="x.png"> more **bold** end. "more **bold** end": the image's own data-src is no tag.
    await showCore("lazy");
    await expectCopy(
      "a document's own data-src is not read as a source tag",
      [{ ...top(0, 1), offset: 1 }, { ...top(0, 3), offset: 4 }],
      point(0, null, 6),
      point(0, 48, 4),
    );
    // <p data-src="1.5">raw para</p> then "next para", selected across both. The whole document: the raw paragraph has
    // nothing before it, so the start is the document's start (RawPoint with no node, offset 0).
    await showCore("lazy2");
    await expectCopy(
      "a raw paragraph with a data-src of its own is raw HTML, copied from the document's start",
      [{ ...top(0, 0), offset: 0 }, { ...top(1, 0), offset: 9 }],
      point(null, null, 0),
      point(32, null, 9),
    );
    // Para., then a raw block that ends the document. "Para.\n\n<div class=\"raw\">\n<p>raw text</p>\n</div>": the end is
    // the document's end.
    await showCore("rawend");
    await expectCopy(
      "a selection into raw HTML at the document's end copies the raw block",
      [{ ...top(0, 0), offset: 0 }, { ...raw, offset: 4 }],
      point(0, null, 0),
      point(null, null, 0),
    );
    // Intro, table, raw block, table, After.
    await showCore("tableraw");
    // "<div class=\"raw\">\n<p>raw text</p>\n</div>": no stray " |" from the row before it.
    await expectCopy(
      "a selection inside a raw block after a table copies the raw block alone",
      [{ ...raw, offset: 2 }, { ...raw, offset: 5 }],
      point(33, null, 1),
      point(82, null, 0),
    );
    // "| c | d |\n|---|---|\n| 3 | 4 |": the second table whole.
    await expectCopy(
      "a start at a raw block's last text before a table copies the table from its first row",
      [{ ...raw, offset: 8 }, { css: "td", nth: 3, text: 0, offset: 1 }],
      point(82, null, 0),
      point(106, null, 1),
    );
    // "Intro.\n\n| a | b |\n|---|---|\n| 1 | 2 |": the first table with its last row's closing pipe.
    await expectCopy(
      "an end at a raw block's first text after a table keeps the table whole",
      [{ ...top(0, 0), offset: 0 }, { ...raw, offset: 0 }],
      point(0, null, 0),
      point(33, null, 1),
    );
    // see ![alt](x.png), then ![solo](y.png). "see ![alt](x.png)" and "![solo](y.png)".
    await showCore("image");
    await expectCopy(
      "an end just after an image keeps the image",
      [{ ...top(0, 0), offset: 0 }, { css: ":scope > p", nth: 0, offset: 2 }],
      point(0, null, 0),
      point(0, 4, 0),
    );
    await expectCopy(
      "an image-only paragraph selected whole copies its image",
      [{ css: ":scope > p", nth: 1, offset: 0 }, { css: ":scope > p", nth: 1, offset: 1 }],
      point(19, null, 0),
      point(19, 19, 0),
    );
    // A paragraph above a diagram. "before para": the diagram's style sheet is no text.
    await showCore("mermaid", { hasMermaid: true });
    await expectCopy(
      "an end at a diagram's first label does not count its style sheet",
      [{ ...top(0, 0), offset: 0 }, { label: "A", offset: 0 }],
      point(0, null, 0),
      point(13, null, 0),
    );
    // A README's raw HTML header: an image-only block counts as selected when the selection covers its image, so a copy
    // from the top starts at the document's start. Each expected pair maps through RawSelection.Extract to the whole
    // document, or to the whole document up to "Some" for the second.
    const selectAllAndCopy = async () => {
      await page.locator("#yamv-content > p").last().click();
      await page.keyboard.press("Control+a");
      return page.evaluate(() => {
        window.__messages = [];
        window.yamv.copyRaw();
        return window.__messages.filter((message) => message.type === "rawSelection");
      });
    };
    const expectAll = async (name, start, end) => {
      const posted = await selectAllAndCopy();
      const ok = posted.length === 1
        && JSON.stringify([posted[0].start, posted[0].end]) === JSON.stringify([start, end])
        && posted[0].generation === generation;
      check(name, ok, posted);
    };
    // <p align="center">\n  <img src="logo.png" alt="Logo">\n</p>, <h1 align="center">Project</h1>, Some intro text.
    await showCore("readmea");
    await expectAll("select all in a README with a logo header copies the whole document", point(null, null, 0), point(92, null, 16));
    await expectCopy(
      "a selection from the content's start keeps a README's logo and raw heading",
      [{ css: "*", nth: 0, offset: 0, content: true }, { ...top(1, 0), offset: 4 }],
      point(null, null, 0),
      point(92, null, 4),
    );
    // A one-line logo, a row of badge links, # Project, Some intro text.
    await showCore("readmeb");
    await expectAll("select all in a README with logo and badges copies the whole document", point(null, null, 0), point(220, null, 16));
    // Para., then a one-line logo ending the document. "Para.\n\n<p align=\"center\"><img src=\"logo.png\" alt=\"Logo\"></p>".
    await showCore("logobottom");
    await expectCopy(
      "a selection through a logo at the document's end keeps the logo",
      [{ ...top(0, 0), offset: 0 }, { css: "*", nth: 0, offset: -1, content: true }],
      point(0, null, 0),
      point(null, null, 0),
    );
    // A document that is only raw HTML: the document's start and end, so the raw block is copied whole.
    await showCore("rawonly");
    await expectCopy(
      "a selection in a document of only raw HTML names the document's edges",
      [{ ...raw, offset: 2 }, { ...raw, offset: 5 }],
      point(null, null, 0),
      point(null, null, 0),
    );
    await showCore("find");
    const blank = await copyIn([{ ...top(0, 0), offset: 3 }, { ...top(0, 0), offset: 4 }]);
    check("a selection of only whitespace posts nothing", blank.length === 0, blank);

    // A reload builds off screen while the old page and its selection stay: a copy before the swap names the old page.
    await showCore("mathold");
    const shownGeneration = generation;
    await copyIn([{ ...top(0, 0), offset: 0 }, { ...top(0, 0), offset: 6 }]);
    const reloadCopy = await page.evaluate(async ([html, options]) => {
      const done = window.yamv.render(html, options);
      window.__messages = [];
      window.yamv.copyRaw();
      const posted = window.__messages.filter((message) => message.type === "rawSelection");
      await done;
      return posted;
    }, [coreHtml.mathnew, { ...baseOptions, documentPath: "C:\\docs\\mathold.md", generation: generation + 1, hasMath: true }]);
    check(
      "a copy while a reload is staged posts the generation of the page on screen",
      reloadCopy.length === 1 && reloadCopy[0].generation === shownGeneration,
      reloadCopy,
    );
  }

  // The page reports the width its text column is laid out in, which a classic scrollbar narrows. Playwright hides
  // scrollbars by default, so these checks run in a browser of their own that shows them, as WebView2 does.
  {
    const scrollbarBrowser = await chromium.launch({ headless: true, ignoreDefaultArgs: ["--hide-scrollbars"] });
    try {
      const shown = await scrollbarBrowser.newPage({ viewport: { width: 1010, height: 662 } });
      shown.setDefaultTimeout(5 * 60_000);
      await shown.addInitScript(() => {
        window.__messages = [];
        window.invokeCSharpAction = (body) => window.__messages.push(JSON.parse(body));
      });
      await shown.goto(templateUrl);
      await shown.waitForFunction(() => window.__messages.some((message) => message.type === "ready"));
      const longHtml = Array.from({ length: 120 }, (_, index) => `<p>Paragraph ${index}.</p>`).join("\n");
      const shortHtml = "<p>One paragraph.</p>";
      const renderShown = (html, generation) =>
        shown.evaluate(([h, o]) => window.yamv.render(h, o), [html, { ...baseOptions, contentWidth: "none", generation }]);
      // The viewport messages of a render, after two frames so a resize report the render caused has been posted.
      const viewportsOf = (generation) =>
        shown.evaluate(async (g) => {
          await new Promise((resolve) => window.requestAnimationFrame(() => window.requestAnimationFrame(resolve)));
          return window.__messages.filter((message) => message.type === "viewport" && message.generation === g);
        }, generation);

      await renderShown(longHtml, 30);
      const longReports = await viewportsOf(30);
      check(
        "a document taller than the view reports the 995 px its scrollbar leaves of 1010, once",
        longReports.length === 1 && longReports[0].value === 995,
        longReports,
      );
      await renderShown(shortHtml, 31);
      const shortReports = await viewportsOf(31);
      check("a short document that replaces it reports 1010 for the newer render", shortReports.at(-1)?.value === 1010, shortReports);
      await renderShown(shortHtml, 32);
      const sameReports = await viewportsOf(32);
      check("a new render reports its width even when it is the width already reported", sameReports.at(-1)?.value === 1010, sameReports);

      // A resize while a render waits for its math does not report for that render before it has rendered.
      const mathHtml = `${longHtml}\n<p><span class="math">\\(x^2\\)</span></p>`;
      const mathRender = shown.evaluate(
        ([h, o]) => window.yamv.render(h, o),
        [mathHtml, { ...baseOptions, contentWidth: "none", generation: 33, hasMath: true }],
      );
      await shown.setViewportSize({ width: 900, height: 662 });
      await mathRender;
      const mathReports = await viewportsOf(33);
      const mathMessages = await shown.evaluate(() => window.__messages.filter((m) => m.generation === 33).map((m) => m.type));
      check(
        "a render's first viewport report comes after its rendered message",
        mathMessages.indexOf("rendered") >= 0 && mathMessages.indexOf("rendered") < mathMessages.indexOf("viewport"),
        { mathMessages, mathReports },
      );
    } finally {
      await scrollbarBrowser.close();
    }
  }
} finally {
  await browser.close();
}

console.log(`${total - failed} of ${total} checks passed`);
process.exitCode = failed > 0 ? 1 : 0;
