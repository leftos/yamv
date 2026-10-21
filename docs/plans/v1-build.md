# YAMV (Yet Another Markdown Viewer) — build plan

## Context

The user wants a cross-platform (Windows + macOS) C# markdown viewer:

- No arguments → the **main instance**: it reopens the documents that were open last time, with a tree on the **right** grouping them by git repo (or, outside a repo, by the folder they're in). Only one main instance may run; a second no-arg launch focuses the existing one and exits.
- One or more file arguments → a **viewer instance** (its own process) showing just those files. It is never persisted and doesn't touch the main instance.
- Beautiful, few frills.

### Interview answers so far

| Topic | Decision |
|---|---|
| Rendering | Markdig → HTML in Avalonia's official `NativeWebView` (WebView2 / WKWebView) |
| Navigation | Tree only, no tabs; close button on row hover |
| Grouping | Root = git work-tree root, else the file's parent folder; real subfolders below it, single-child chains compacted (`docs/plans`) |
| v1 extras | Live reload (keeps scroll), find in page, drag-drop + File > Open, relative `.md` links open in-app (web links go to the OS browser) |
| macOS Finder open while running | Opens a viewer window in the running process (not persisted) |
| Viewer tree | Shown only when the viewer holds 2+ files |
| Packaging | Publish scripts, a macOS `.app` with an Info.plist that declares `.md`, and Windows per-user `.md` registration through `--register` / `--unregister` |
| Repo | Local git plus a new **public** GitHub repo `leftos/yamv` |

### Researched stack (nuget.org, 2026-10)

- `Avalonia` **12.1.4** (MIT, net10.0). `Avalonia.Controls.WebView` **12.1.1** (MIT). It provides `NavigateToString(html, baseUri)`, page → host messages (`invokeCSharpAction` → `WebMessageReceived`), `InvokeScript`, and `NavigationStarted` with `Cancel`, plus `WebResourceRequested`.
- `Markdig` **1.4.2** (BSD-2).
- Single instance: a **lock file** (a `FileStream` with `FileShare.None` in the per-user app-data dir, held for the process lifetime). A named mutex is avoided because its behaviour differs on macOS. Pipe: a `NamedPipeServerStream` with `CurrentUserOnly`, in byte mode with length-prefixed UTF-8 messages, its endpoint derived from the lock path (`main.sock` beside the lock on Unix, a hashed name on Windows). A secondary no-arg launch retries the connect briefly.
- Git root: walk up from the file to the first `.git`, which may be a dir or a file (worktree/submodule). Cache results per directory. No LibGit2Sharp.
- Re-check every version at implementation time (language-conventions rule).

## Step 1 — Scaffold the repo (one commit)

- `git init`, then `gh repo create leftos/yamv --public --source . --push` (approved by this plan).
- Add `.gitattributes` (LF; CRLF only for `.bat`/`.cmd`), `.editorconfig`, `.gitignore` (bin/obj/.tmp/publish), and prek with `mixed-line-ending --fix=lf` plus `dotnet format` hooks.
- Docs skeleton: `CLAUDE.md`, `README.md`, `CHANGELOG.md`, `docs/README.md` (links the architecture doc and glossary), `docs/ARCHITECTURE.md` from `~/.claude/docs/templates/ARCHITECTURE.md`, `docs/glossary.md` (**main instance**, **viewer instance**, **group root**), and `docs/plans/MAIN.md` holding the steps below as checkboxes.
- Solution (`Yamv.slnx`, net10.0, `Directory.Build.props` with nullable, warnings-as-errors, analyzers per the `language-conventions` C# section):
  - `src/Yamv.Core`: pure logic, no Avalonia
  - `src/Yamv.App`: Avalonia 12 desktop app; the output assembly is `yamv`
  - `tests/Yamv.Core.Tests`

## Step 2 — Mocks and the visual interview (blocks all UI work)

Mocks are HTML pages in `docs/design/mocks/<name>/index.html`, with screenshots beside them. The review copy goes in `X:\dev\yamv\.tmp\mocks\<name>\index.html` via `~/.claude/tools/mock-embed/embed_media.py`. Each mock is rendered headless (`npx playwright screenshot --full-page`) and looked at before it is asked about, then committed.

1. **Overall look**, as three lanes, each shown in light and dark with a real document (headings, a table, code, a task list, a blockquote, an image):
   - **Paper**: warm off-white, serif body text, a quiet sidebar.
   - **Studio**: neutral, GitHub-like sans, crisp borders.
   - **Ink**: dark-first, high contrast, coloured accents.
2. **Tree and window chrome** for the chosen look: sidebar width, the repo/folder group header style (a git badge and a full-path tooltip vs the path as a subtitle), row density, the hover close button, and the title bar (custom extended client area showing the doc name and path vs the native title bar).
3. **Edge states**:
   - a file from the last session that no longer exists: a greyed "missing" row the user can dismiss, vs dropped silently with a one-time notice;
   - the main window's empty state;
   - the find bar.

The user's choices are recorded in `docs/design/look.md`, and the mocks stay as its reference.

## Step 3 — Core logic, test-first (`Yamv.Core`, one commit per bullet)

- `GitRootFinder.Find(path)`: walk up for a `.git` dir or file; per-directory cache; case-insensitive comparison on Windows/macOS.
- `DocumentGrouper.Group(IEnumerable<string> paths)` → an immutable tree (`GroupNode` → `FolderNode` → `DocumentNode`):
  - group root = git root ?? parent folder;
  - compact single-child folder chains;
  - sort folders before files, case-insensitively;
  - name groups by repo folder name, or by the full folder path with `~` on macOS.
  - Tests cover: nested repos (a submodule wins), worktree `.git` files, a file at the repo root, mixed repo and non-repo files, two repos with the same name (disambiguated by parent folder), and drive roots.
- `SessionStore`: JSON at `%APPDATA%\YAMV\session.json` or `~/Library/Application Support/YAMV/session.json`. It holds the open paths, the selected doc, per-doc scroll fraction, window bounds, sidebar width, collapsed groups and theme. Saves are debounced and written atomically (temp file + replace). A corrupt file is logged and moved to `session.json.bad`, never silently replaced by defaults. Uses System.Text.Json source generation (AOT/trim-safe).
- `InstanceCoordinator`:
  - `TryAcquireMainLock()` takes the lock file;
  - on failure, the static `SignalAsync(lockFilePath, "activate", timeout, log)` sends the message over the pipe and the process exits;
  - the main instance runs the pipe server with `StartServer(onMessage)`;
  - the main instance also writes its pid and start time to a sibling file (`main.lock` → `main.pid`), not into the locked file, and `TryReadMainPid(lockFilePath, log)` gives a second launch the pid only when that process is running and its start time matches.
  - Viewer launches (with file args) skip the coordinator entirely.
  - Tests run two coordinators in-process with unique names.
- `LaunchOptions.Parse(args)` → `Main` | `Viewer(files)` | `Register` | `Unregister`. A folder argument is refused ("Not a file: {full path}", exit 2); a path that does not exist is kept and opens as a missing document. Relative paths are resolved against the cwd.
- `MarkdownRenderer.Render(text)` → an HTML body string:
  - Markdig with `UseAdvancedExtensions()` (tables, task lists, footnotes, autolinks, emphasis extras), plus front matter (hidden), `UseMathematics`, and diagram blocks;
  - heading ids for anchors;
  - a source-line map (`data-line` attrs) used to restore scroll.
  - Tests use snapshot-style HTML assertions on a few fixtures.

## Step 4 — App shell (`Yamv.App`), built to the chosen mock

- **Spike** (done in the document-view work): neither `NavigateToString(html, baseUri)` (the base is ignored) nor a virtual host through `WebResourceRequested` (observe-only in 12.1.1) works, so the page assets are extracted to a versioned folder under the local app-data dir and the page loads over `file://`. The CI macOS run and the user's Mac test confirm WKWebView.
- The page is a `template.html` loaded once, with embedded resources `yamv.css` (the chosen theme, light and dark via CSS variables) and `yamv.js`, both bundled, with no CDN. Switching docs calls `yamv.render(bodyHtml, scrollFraction)` through `InvokeScript`, so the WebView never reloads.
  - highlight.js is bundled.
  - Mermaid and KaTeX are bundled but loaded only when a doc contains a diagram or math block.
- `yamv.js` posts messages to the host: `scroll` (debounced, saved per doc), `link` (every `<a>` click is intercepted), and `find-result`. Anchor links scroll inside the page. `NavigationStarted` cancels any other navigation as a backstop.
- `MainWindow` has the document on the left and the `TreeView` sidebar on the right with a `GridSplitter`. The width is persisted.
  - Rows show a close button on hover.
  - Context menu: Reveal in Explorer/Finder, Copy path, Close, Close group.
  - The title follows the chosen chrome.
  - The theme follows the OS, with a manual override in the theme settings window (YAMV-11).
  - MVVM uses CommunityToolkit.Mvvm (source generators; the dependency is justified because it is Avalonia's default template choice and avoids hand-written INotifyPropertyChanged). One `DocumentWindowViewModel` serves both modes, with a `Persist` flag that is true only for main.

## Step 5 — Launch modes

- `Program.Main`:
  - `Register`/`Unregister` → registry work, then exit.
  - `Viewer(files)` → a window with `Persist = false`; the sidebar is visible only when the window holds 2+ files.
  - `Main` → the coordinator; if not main, signal and exit; else restore the session (missing files handled per Step 2). A second launch brings the main window forward as `docs/ARCHITECTURE.md` describes (`SecondLaunch`, the pid file, `AllowSetForegroundWindow`).
- macOS: handle `IActivatableLifetime` file activation by opening a viewer window in the running process.
  - Cold start from a Finder double-click: hold main-window creation until launch-time file events have arrived, so that a Finder-opened file starts only a viewer window, and Dock/no-file launches start the main window.
  - Dock-icon reopen with no windows shows the main window.
  - This logic is verified on the user's Mac, since it can't be exercised on this Windows machine.

## Step 6 — Extras

- **Live reload**: a `FileSystemWatcher` per open file's directory, filtered to open files, with a trailing 150 ms debounce and a 1 s maximum wait (YAMV-9 ruling). It handles editors that save by write-temp-then-rename. The doc re-renders at the same source line (the `data-line` anchor, also on a tab switch and session restore), with find keeping its query and the match at the same source position. A deleted file keeps its page for a 500 ms grace, then shows the missing state; a file in use keeps a shown page for up to 10 s.
- **Find**: Ctrl/Cmd+F opens the find bar from the mock. JS wraps matches in `<mark>`, Enter / Shift+Enter step through them, the bar shows a match count, and Esc clears.
- **Drag-drop and File > Open** (multi-select, `.md`/`.markdown`/`.mdx`/`.txt`): add to the current window; only the main window persists.
- **Links**: decided by Core's link policy under the rulings on YAMV-9: `http(s)`/`mailto` → the OS browser; a local document type (`.md .markdown .mdx .txt`) → opened in this window and added to its tree, with a `#fragment` scrolled to, missing or outside the group root included; another local file inside the group root (checked after resolving links) → the OS default app, at once for the viewable-type allowlist and after a confirm otherwise; everything else is ignored.

## Step 7 — Windows registration (`--register` / `--unregister`)

- Writes under HKCU only: a `HKCU\Software\Classes\YAMV.Markdown` ProgID with an icon and `shell\open\command` → `"<exe>" "%1"`, and `.md`/`.markdown` `OpenWithProgids` entries, so YAMV appears in Open with. The user's default handler is not taken over (Windows 11 requires the user to pick it). `Applications\yamv.exe` gets `SupportedTypes`. Then `SHChangeNotify`.
- Multi-select in Explorer: check how Explorer passes several files for this verb (`MultiSelectModel`). If it starts one process per file, that is acceptable and documented.
- `--unregister` removes exactly the keys it created. Registration logic sits behind `OperatingSystem.IsWindows()`, and the tests run against a temporary HKCU subkey.

## Step 8 — Packaging and CI

- `scripts/publish.ps1 -Rid win-x64|osx-arm64|osx-x64` runs a self-contained, single-file `dotnet publish` into `publish/<rid>/`.
- `scripts/make-app.sh`, run on macOS, assembles `YAMV.app` (`Contents/MacOS`, `Info.plist` with `CFBundleDocumentTypes` for `net.daringfireball.markdown`, `LSHandlerRank=Alternate`, and the icon `.icns`), with chmod +x and an ad-hoc `codesign -s -` so Gatekeeper allows a local run. No notarization.
- App icon: a simple SVG mark (checked with `render-svg`), exported to `.ico`/`.icns`.
- `.github/workflows/ci.yml` (pinned actions, `actionlint` + `zizmor` clean):
  - a build + test matrix on `windows-latest` and `macos-latest`;
  - on `macos-latest`, `make-app.sh` runs and uploads `YAMV.app.zip`;
  - on Windows, the `win-x64` exe is uploaded.

## Step 9 — Docs and finish

- `docs/ARCHITECTURE.md` fully filled in (Task Index, Layers: Core never references Avalonia; Integration Footguns: `template.html` ↔ `yamv.js` message names ↔ `WebMessageReceived` handler; Test locations).
- README: usage, the launch modes and the registration command.
- `CHANGELOG.md` `## Unreleased` bullets, and the plan items ticked.

## Execution

- Per CLAUDE.md, the main session orchestrates via `plan-execution`; source and test edits go to `implementer` briefs. There is one commit per item, and pushes go to the public repo at milestones.
- Builds and tests run through `gate run --slot heavy`, building `-c Release` and then running `dotnet test Yamv.slnx -c Release --no-build`. Test mode is `stable`, so every landing runs the full suite.

## Verification

1. `dotnet test` green, with zero warnings.
2. On Windows, via the `run` skill with screenshots:
   - a first no-arg launch shows the empty state; add 3 files from 2 repos plus 1 loose file and check that the grouping matches the mock;
   - quit and relaunch → the same docs, selection and scroll are restored;
   - a second no-arg launch → the first window comes to the front and the new process exits;
   - `yamv a.md b.md` → a separate window with a tree; `yamv a.md` → no tree; neither changes `session.json`;
   - edit an open file → it re-renders at the same scroll;
   - Ctrl+F finds matches; a relative `.md` link opens in-app; a web link opens the browser; a relative image renders; Mermaid and code highlighting render;
   - `yamv --register` → YAMV appears in Explorer "Open with" for `.md`; `--unregister` removes it.
3. CI is green on both OSes. The `.app` artifact is sent to the user's MacBook (the `localsend` skill), where the user checks a Finder open while running (viewer window), a Finder open on a cold start, a Dock launch (main window with the restored session) and WKWebView image loading.
