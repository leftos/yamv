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
  await page.waitForTimeout(1200);
  check("meta refresh does not navigate", page.url() === templateUrl, page.url());
  check("no request to the refresh or link targets", !requested.some((url) => /refresh\.example|link\.example/.test(url)), requested);

  // Step 11: C# fences are highlighted as csharp.
  const code = await page.evaluate(() => {
    const element = document.getElementById("csharp");
    return { classes: element.className, keywords: element.querySelectorAll(".hljs-keyword").length };
  });
  check("language-c# is highlighted as csharp", code.classes.includes("language-csharp") && code.keywords > 0, code);

  // Step 4: only primary and middle clicks post links.
  await clearMessages();
  await page.click("#link", { button: "right" });
  await page.waitForTimeout(200);
  all = await messages();
  check("right-click on a link posts nothing", !all.some((m) => m.type === "link"), all);
  await page.keyboard.press("Escape");
  await page.click("#link", { button: "middle" });
  await page.click("#link", { button: "left" });
  await page.waitForTimeout(200);
  const links = (await messages()).filter((m) => m.type === "link");
  check("middle then left click post buttons 1 and 0", links.length === 2 && links[0].button === 1 && links[1].button === 0 && links[1].href === "other.md", links);
  // The host refuses a file: link with a host and raises one outside the group root; the page posts both as written.
  await clearMessages();
  await page.click("#host-link");
  await page.click("#outside-link");
  await page.waitForTimeout(200);
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

  // Step 11: the render's fragment is decoded, and scroll messages carry the generation.
  await clearMessages();
  await render({ ...baseOptions, generation: 8, fragment: "caf%C3%A9" });
  const scrolled = await page.evaluate(() => window.scrollY);
  check("an encoded fragment scrolls to its element", scrolled > 0, scrolled);
  await page.waitForTimeout(100);
  await page.evaluate(() => window.scrollTo(0, 10));
  await page.waitForTimeout(400);
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
    await page.waitForTimeout(50);
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
} finally {
  await browser.close();
}

console.log(`${total - failed} of ${total} checks passed`);
process.exitCode = failed > 0 ? 1 : 0;
