# YAMV — architecture

YAMV is a markdown viewer for Windows and macOS. `Yamv.Core` holds every piece of logic that can run without a UI (grouping, session, single-instance, rendering markdown to HTML); `Yamv.App` is the Avalonia 12 shell around a `NativeWebView`. Core references no UI framework. Terms are in the [glossary](glossary.md).

## Task Index

| Task | Files, in order | Deep doc |
|---|---|---|
| Change how documents are grouped in the tree | `src/Yamv.Core/Grouping/` → `tests/Yamv.Core.Tests/Grouping/` | |
| Change the theme file format, a token, or a built-in theme | `src/Yamv.Core/Theming/ThemeTokens.cs` → `ThemeParser.cs` → `ThemeSerializer.cs` → `themes/yamv-theme.schema.json` → `src/Yamv.Core/Theming/BuiltIn/` → `tests/Yamv.Core.Tests/Theming/` | [themes.md](design/themes.md) |
| Change how themes are listed, imported, saved or deleted | `src/Yamv.Core/Theming/ThemeCatalog.cs` → `ThemeIds.cs` → `tests/Yamv.Core.Tests/Theming/ThemeCatalogTests.cs` | [themes.md](design/themes.md) |

## Layers

- **`Yamv.Core`** (`src/Yamv.Core/`): owns grouping, git-root discovery, the session file, the single-instance lock and pipe, launch-argument parsing and markdown → HTML. References Markdig and the BCL only; never Avalonia. One main instance per user is held by a lock file (flock on Unix) plus a per-user named pipe that later launches signal over; a named mutex is not used, because its behaviour differs on macOS. Theming (`Theming/`) parses theme files, resolves a theme for light or dark, builds its CSS (`ThemeCss`), and keeps the catalog of built-in and user themes (`ThemeCatalog`); it owns no watcher and no UI.
- **`Yamv.App`** (`src/Yamv.App/`): owns windows, the tree, the WebView page (`Assets/web/`) and platform glue (macOS activation, Windows registration). References Core.

## Integration Footguns

- A theme token rename or a new token also changes the page's `yamv.css` variables (`src/Yamv.App/Web/`) and the schema `themes/yamv-theme.schema.json`.
- Theme colours are CSS hex, `#rrggbbaa`; Avalonia's `Color.Parse` reads 8 digits as `#aarrggbb`, so brushes are built from `ColorValue`'s R, G, B and A, never by parsing the hex.
- `ThemeCatalog.LoadAll` can list two entries with one id (an invalid user file beside the built-in it shadows): look a theme up by id preferring the valid entry.

## Test locations

- `tests/Yamv.Core.Tests`: everything in Core. A new Core type gets a test file at the mirrored path.

## Deep docs

- [Themes](design/themes.md): the theme file format, the tokens, errors and the trust model.
