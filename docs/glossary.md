# Glossary

- **Appearance**: Match system, Light or Dark — which variant of the current theme family is shown.
- **Asset cache**: the versioned folder under the local app-data dir (`YAMV/web/{assembly version id}/`) that the document page's template, CSS, JS, fonts and vendored libraries are extracted to, so the page loads over `file://`.
- **Group root**: the folder a document is grouped under in the tree: its git work-tree root, or its parent folder when it is not in a repo.
- **Main instance**: the one YAMV process started with no file arguments. It remembers its open documents between runs; a second no-arg launch brings it to the front and exits.
- **Missing document**: an open document whose file no longer exists; it stays in the tree, greyed, until removed or until the file comes back.
- **Theme family**: one `.yamv-theme.json` file, holding a light variant, a dark variant or both ([themes.md](design/themes.md)).
- **Token**: a named theme value (a colour, font, size) that drives both the document page (as a `--yamv-*` CSS property) and the app chrome.
- **Variant**: the light or dark half of a theme family.
- **Viewer instance**: a YAMV window started with file arguments (or, on macOS, a Finder open). It shows only those files and remembers nothing.
