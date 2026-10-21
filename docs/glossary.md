# Glossary

- **Activation gate**: holds an `activate` request that arrives before the main window exists and delivers it once the window is shown, coalescing duplicates.
- **Appearance**: Match system, Light or Dark — which variant of the current theme family is shown.
- **Asset cache**: the versioned folder under the local app-data dir (`YAMV/web/{assembly version id}/`) that the document page's template, CSS, JS, fonts and vendored libraries are extracted to, so the page loads over `file://`.
- **Chrome**: the app's own UI around the document (title bar, sidebar tree, dialogs), drawn with Avalonia from the theme's chrome tokens, as opposed to the document page.
- **Document surface**: what the window logic sees of the document page (`IDocumentSurface`, implemented by `DocumentView`); tests substitute a fake.
- **Grace period**: the 300 ms after framework initialization on macOS during which launch-time file opens may arrive, before the main window is created.
- **Group root**: the folder a document is grouped under in the tree: its git work-tree root, or its parent folder when it is not in a repo.
- **Launch arbiter**: the Core state machine that turns framework, file-open and Dock/activate events of a macOS launch into what it decides: a main window, a viewer window, or activating the main window.
- **Main instance**: the one YAMV process started with no file arguments. It remembers its open documents between runs; a second no-arg launch brings it to the front and exits.
- **Missing document**: an open document whose file no longer exists; it stays in the tree, greyed, until removed or until the file comes back.
- **Open with registration**: what `yamv --register` writes on Windows so YAMV appears in Explorer's Open with for `.md` and `.markdown`: the `YAMV.Markdown` ProgID, an `OpenWithProgids` entry per extension and the `Applications\yamv.exe` key, all under the current user. It never sets the default handler.
- **Persist**: a window's flag saying its documents, selection and layout are saved to the session file; true only for the main instance's window.
- **ProgID**: a Windows programmatic identifier, a registry key naming a file type and how to open it; YAMV's is `YAMV.Markdown`.
- **Theme family**: one `.yamv-theme.json` file, holding a light variant, a dark variant or both ([themes.md](design/themes.md)).
- **Token**: a named theme value (a colour, font, size) that drives both the document page (as a `--yamv-*` CSS property) and the app chrome.
- **Tree order**: the order documents appear in the sidebar tree (groups, then folders before files, case-insensitively), which Close, Ctrl+Tab and Ctrl+Shift+Tab follow; it differs from the order documents were opened.
- **Variant**: the light or dark half of a theme family.
- **Viewer instance**: a YAMV window started with file arguments (or, on macOS, a Finder open). It shows only those files and remembers nothing.
