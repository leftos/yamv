# YAMV look

The chosen design. The mocks it was chosen from are kept beside it as reference: [look](mocks/look/index.html) ([screenshot](mocks/look/look.png)) and [chrome](mocks/chrome/index.html) ([screenshot](mocks/chrome/chrome.png)). A UI change that departs from this page gets a mock first.

## Themes

- All six mock variants ship as built-in themes, as three theme families: **Paper** (light, dark), **Studio** (light, dark) and **Ink** (dark, light). Studio is the default.
- Appearance is **Match system**, **Light** or **Dark**. Match system follows the OS and picks the matching variant from the current family. If the family has only one variant, that variant is always used.
- Themes are JSON files that can be shared, and a user can add their own. The file format is in [themes.md](themes.md).
- Bundled fonts: Inter (UI and Studio/Ink body), Source Serif 4 (Paper), JetBrains Mono (code, Ink headings). All three are OFL and are embedded so Windows and macOS render alike.

## Window

- **Custom title bar**, drawn into the window's title area. It shows the document name in semibold, then its location inside its group in muted text (`yamv / docs / plans`). Window controls are on the right on Windows; on macOS the traffic lights stay on the left. The bar is 38 px tall and takes the theme's chrome colours.
- The document pane is on the left, centred to the theme's measure. The **tree sidebar is on the right** and 250–280 px wide, resizable by a splitter, and its width is remembered.
- There are no tabs. The tree is the only list of open documents.

## Tree

- **Group header**: the name in semibold (the repo folder name, or the folder path with `~` for home), with a `GIT` or `DIR` badge on the right. Hovering shows the full path in a tooltip. Headers collapse with a chevron.
- Folder rows are muted and have a chevron. Compacted chains are joined with `/` (`docs/plans`).
- Document rows have a file icon. The selected row gets the selection fill and a 3 px accent bar on its left edge. A hovered row shows a `×` close button on its right.
- Context menu: Reveal in Explorer/Finder, Copy path, Close, Close group.

## Edge states

- **Missing document** (gone on restore or deleted while open): the row stays, greyed and struck through, with a `MISSING` tag. Selecting it shows "This file is no longer there", its full path, **Locate…** and **Remove from list**, and the note that YAMV keeps watching. If the file reappears, it reopens on its own.
- **Empty main window**: a dashed `md` mark, "No documents open", "Drop markdown files here, or open some. They stay open next time.", an **Open files…** button and the Ctrl/Cmd+O hint.
- **Find bar**: Ctrl/Cmd+F floats it at the document's top-right, with a field, "n of m", up/down and close. Matches are highlighted and the current one is stronger. Enter and Shift+Enter step through matches; Esc closes and clears.

## Theme settings window

- The **visual editor**:
  - **Left:** the theme list, in two sections, Built-in and Yours.
  - **Middle:** every token, grouped as in the [token table](themes.md#tokens) (Page, Chrome, Code, Syntax, Type, Layout). It has Shared / Light / Dark tabs, colour swatches with hex fields and a picker, and value fields for fonts and sizes.
  - **Right:** a live preview.
- Footer: **Import…**, **Export…**, **Edit as text** (opens the file in the default editor), **Revert**, **Save**. A built-in theme is read-only: editing one starts with "Duplicate" into Yours.
- Editing a theme file outside the app re-applies it live.
