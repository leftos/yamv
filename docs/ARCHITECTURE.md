# YAMV — architecture

YAMV is a markdown viewer for Windows and macOS. `Yamv.Core` holds every piece of logic that can run without a UI (grouping, session, single-instance, rendering markdown to HTML); `Yamv.App` is the Avalonia 12 shell around a `NativeWebView`. Core references no UI framework. Terms are in the [glossary](glossary.md).

## Task Index

| Task | Files, in order | Deep doc |
|---|---|---|
| Change what a launch, Finder open, Dock click or `activate` opens | `src/Yamv.Core/Launch/LaunchArbiter.cs` → `ActivationGate.cs` → `tests/Yamv.Core.Tests/Launch/` | |
| Change how the main instance is held or signalled | `src/Yamv.Core/Instance/InstanceCoordinator.cs` → `ServerFailureLog.cs` → `InstanceMessages.cs` → `tests/Yamv.Core.Tests/Instance/` | |
| Change how documents are grouped in the tree | `src/Yamv.Core/Grouping/` → `tests/Yamv.Core.Tests/Grouping/` | |
| Change the theme file format, a token, or a built-in theme | `src/Yamv.Core/Theming/ThemeTokens.cs` → `ThemeParser.cs` → `ThemeSerializer.cs` → `themes/yamv-theme.schema.json` → `src/Yamv.Core/Theming/BuiltIn/` → `tests/Yamv.Core.Tests/Theming/` | [themes.md](design/themes.md) |
| Change how a document looks or behaves in the page (rendering, find, links, images) | `src/Yamv.App/Web/yamv.js` → `Web/yamv.css` → `Web/template.html` → `src/Yamv.App/Controls/DocumentView.cs` → `tools/dev/page-check/` | |
| Change how themes are listed, imported, saved or deleted | `src/Yamv.Core/Theming/ThemeCatalog.cs` → `ThemeIds.cs` → `tests/Yamv.Core.Tests/Theming/ThemeCatalogTests.cs` | [themes.md](design/themes.md) |

## Layers

- **`Yamv.Core`** (`src/Yamv.Core/`): owns grouping, git-root discovery, the session file, the single-instance lock and pipe, launch-argument parsing and markdown → HTML. References Markdig and the BCL only; never Avalonia. One main instance per user is held by a lock file (flock on Unix) plus a per-user named pipe that later launches signal over; a named mutex is not used, because its behaviour differs on macOS. Theming (`Theming/`) parses theme files, resolves a theme for light or dark, builds its CSS (`ThemeCss`), and keeps the catalog of built-in and user themes (`ThemeCatalog`); it owns no watcher and no UI.
- **`Yamv.App`** (`src/Yamv.App/`): owns windows, the tree, the document page (`Web/`, embedded as `Web/...` resources) and platform glue (macOS activation, Windows registration). References Core.
  - `Controls/DocumentView` hosts the page in Avalonia's `NativeWebView`. The package's `WebResourceRequested` is observe-only and `NavigateToString` ignores its base URI, so `WebAssets` extracts the page to the [asset cache](glossary.md) and the page loads over `file://`. Two instances extracting at once race safely: the final folder only appears by renaming a complete staging copy.
  - The page's CSP is `script-src 'self'` with no inline script and no eval. Document HTML is inserted with `innerHTML` (so its scripts never run) after losing `style`, `style=`, `meta`, `base`, `link`, frames, objects and embeds; every image-like URL (`src`, `srcset`, `poster`, SVG `href`) must be http(s), a `data:image/` URL, or a host-less `file:` path inside the group root, so a document cannot reach a network share.
  - The find bar lives inside the page, because Avalonia cannot draw over a native web view (airspace). For the same reason an empty or missing state hides the web view rather than covering it.

## Integration Footguns

- A theme token rename or a new token also changes the page's `yamv.css` variables (`src/Yamv.App/Web/`) and the schema `themes/yamv-theme.schema.json`.
- Theme colours are CSS hex, `#rrggbbaa`; Avalonia's `Color.Parse` reads 8 digits as `#aarrggbb`, so brushes are built from `ColorValue`'s R, G, B and A, never by parsing the hex.
- The page and the host speak by message name: `Web/yamv.js` posts `ready`, `rendered`, `scroll`, `link`, `key`, `find`, `blocked` and `error`, which `DocumentView`'s message handler switches on, and the host calls the `window.yamv` functions `render`, `setTheme`, `setContentWidth`, `find`, `clearFind` and `openFind`. A rename changes both sides, and the `RenderOptions` fields match the `settings.*` names the page reads.
- Core's renderer marks diagrams as `<pre class="mermaid">` and puts `data-line` on the `code` element of a fenced block; the page's diagram loader and scroll restore read those.
- The web view swallows Ctrl/Cmd shortcuts while it has focus; the page forwards them as `key` messages (`KeyForwarded`), so a window shortcut is handled in both `KeyDown` and `KeyForwarded`.
- `InstanceCoordinator.StartServer`'s handler runs on a thread-pool thread: the App posts to the UI thread and never blocks in it (a `Dispose` from inside it waits out its own 5 s bound). `SignalAsync` returning true means the main instance read the frame on Windows, but only that the frame reached the socket's send buffer on Unix.
- `ThemeCatalog.LoadAll` can list two entries with one id (an invalid user file beside the built-in it shadows): look a theme up by id preferring the valid entry.

## Test locations

- `tests/Yamv.Core.Tests`: everything in Core. A new Core type gets a test file at the mirrored path.
- `tools/dev/page-check/`: a headless Chromium check of the document page (`npm install`, then `npm run check`), against a stubbed host.
- `tools/dev/run-hidden.ps1` with `tools/dev/capture-window.ps1`, both run under `tools/hidden-desktop.ps1`: screenshots of the real app off the owner's desktop. `docs/design/fixtures/sample.md` is the sample document.

## Deep docs

- [Themes](design/themes.md): the theme file format, the tokens, errors and the trust model.
