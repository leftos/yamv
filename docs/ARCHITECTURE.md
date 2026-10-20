# YAMV — architecture

YAMV is a markdown viewer for Windows and macOS. `Yamv.Core` holds every piece of logic that can run without a UI (grouping, session, single-instance, rendering markdown to HTML); `Yamv.App` is the Avalonia 12 shell around a `NativeWebView`. Core references no UI framework. Terms are in the [glossary](glossary.md).

## Task Index

| Task | Files, in order | Deep doc |
|---|---|---|
| Change how documents are grouped in the tree | `src/Yamv.Core/Grouping/` → `tests/Yamv.Core.Tests/Grouping/` | |

## Layers

- **`Yamv.Core`** (`src/Yamv.Core/`): owns grouping, git-root discovery, the session file, the single-instance lock and pipe, launch-argument parsing and markdown → HTML. References Markdig and the BCL only; never Avalonia. One main instance per user is held by a lock file (flock on Unix) plus a per-user named pipe that later launches signal over; a named mutex is not used, because its behaviour differs on macOS.
- **`Yamv.App`** (`src/Yamv.App/`): owns windows, the tree, the WebView page (`Assets/web/`) and platform glue (macOS activation, Windows registration). References Core.

## Integration Footguns

(filled in as the app shell lands)

## Test locations

- `tests/Yamv.Core.Tests`: everything in Core. A new Core type gets a test file at the mirrored path.

## Deep docs

(none yet)
