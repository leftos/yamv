# Changelog

## Unreleased

### Added

- A main window that reopens last time's documents, grouped by git repo or folder, at its saved size and place.
- `yamv <files>` opens a viewer window with just those files in the running YAMV, saving nothing; a file already open brings its window forward.
- A second `yamv` with no files brings the running main window forward instead of opening another.
- When the running YAMV doesn't answer a file launch, a box says it is not responding and the file was not opened.
- A YAMV run as Administrator opens beside the normal one, marked Administrator in every window's title bar, and saves no session.
- On macOS, files opened from Finder open in viewer windows, and a Dock click brings the main window back.
- Markdown rendered in the document pane, with Mermaid diagrams and the document's local images.
- An open document reloads when its file changes, keeping your place, your find match and the old page until the new one is ready.
- A missing document offers **Locate…** and **Remove from list**; one that can't be read says why and offers **Retry**.
- Open files with Ctrl+O (Cmd+O), the title bar's **Open…** button, the empty window's **Open files…**, or by dropping them on a window.
- Clicked links open in the browser, as another document in the same window, or in the file's default app.
- A link to a program, script, folder or unlisted file type shows a card asking before it opens.
- Right-click in the tree for Reveal in Explorer or Finder, Copy path, Close and Close group.
- Ctrl+[ and Ctrl+] (Cmd on macOS) or the **⟷** popover set the text column's width, saved per folder or for all documents.
- The theme settings window (**⋯** menu, Ctrl+, or Cmd+,) picks, edits with a live preview, imports and exports themes.
- The theme and Appearance are saved app-wide, and an outside edit of the settings or a theme reaches every open window.
- A toast at the bottom of the document pane says when an action you started fails.
- `--help`, and on Windows `--register` and `--unregister`, which add or remove YAMV in Explorer's Open with for `.md` and `.markdown`.
- A single-file `yamv.exe` for Windows and a `YAMV.app` for macOS.
