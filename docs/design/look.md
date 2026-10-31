# YAMV look

The chosen design. The mocks it was chosen from are kept beside it as reference: [look](mocks/look/index.html) ([screenshot](mocks/look/look.png)) and [chrome](mocks/chrome/index.html) ([screenshot](mocks/chrome/chrome.png)). A UI change that departs from this page gets a mock first.

## Themes

- All six looks in the mock ship as built-in themes, as three theme families with a light and a dark half each: **Paper** (light, dark), **Studio** (light, dark) and **Ink** (dark, light). Studio is the default.
- Appearance is **Match system**, **Light** or **Dark**. Match system follows the OS and picks the matching half from the current family. If the family has only one half, that half is always used.
- Themes are JSON files that can be shared, and a user can add their own. The file format is in [themes.md](themes.md).
- Bundled fonts: Inter (UI and Studio/Ink body), Source Serif 4 (Paper), JetBrains Mono (code, Ink headings). All three are OFL and are embedded so Windows and macOS render alike.

## Window

- **Custom title bar**, drawn into the window's title area. It shows the document name in semibold, then its location inside its group in muted text (`yamv / docs / plans`). Window controls are on the right on Windows; on macOS the traffic lights stay on the left. The bar is 38 px tall and takes the theme's chrome colours.
- The document pane is on the left. Its text column is centred at the content width ([content-width.md](content-width.md)), which is set per folder with the title bar's `⟷` popover. The **tree sidebar is on the right**, 280 px by default and resizable between 200 and 480 px by a splitter, and its width is remembered. It is hidden for a viewer window with one document and for an empty main window.
- There are no tabs. The tree is the only list of open documents.
- **Title-bar tools**, in every document window, viewer windows included: an **Open…** folder button, first of the tools and left of `⟷` (tooltip "Open… (Ctrl+O)", "Open… (⌘O)" on macOS; accessible name "Open") and a `⋯` button right of it that opens a small menu whose entry is "Theme settings…"; later app-wide entries join that menu.
- **Drop overlay**: dragging files over the window shows a tinted overlay below the title bar, covering the document pane and the sidebar, with a dashed accent outline and "Drop to open", or, when the drag holds no openable file, a grey refusal "Drop .md, .markdown, .mdx or .txt files". It is its own window over the page, as the width popover is.
- **Toast**: a failed or refused operation the user started shows a toast in the window, never only a line in the log (the log line stays as well). It is a small rounded card at the bottom centre of the document pane with an icon, one line of text and a close ×; it fades after 5 s, counting only while the window is active, and at most three stack upward, the oldest first. The same text shown again while its card is up adds no card: the card shows a count (×2, ×3) and its 5 s start over.

  The document is a native web view nothing can be drawn over, so a toast is its own borderless window over the page, as the width popover is ([mock](mocks/reload-and-drop/index.html), lane A).
- **Confirm card**: a link to a file or folder inside the group root that YAMV does not show (not a document, not a viewable type) asks first, in a card over the document pane with a scrim, the title bar and tree staying live ([mock](mocks/toast-repeat-and-confirm/index.html)). A shortcut's card says it can start any program or open any address.

  The title is "Open {name}?" and the buttons are Open and Cancel; the body says what it is and what will open it, e.g. "It's a PowerShell script (.ps1). YAMV doesn't show this type; it will open with the app Windows uses for it." An unlisted type reads "It's a .xyz file.", no extension "It's a file with no type.", a folder "It's a folder. YAMV doesn't show folders; it will open in Explorer." (Finder on a Mac, where a folder with an extension, such as a `.app`, is worded by that extension).

  Cancel has the focus. Esc and Enter cancel, Enter opens only with the focus on Open, and a click on the scrim does nothing. A document switch, minimising, closing the window or a newer link click cancels the card. It is its own window over the page, as the toast is.
- **Tail** (the spec for a switch not built yet; the mock is pending): a document short enough to fit in the view is at both its top and its bottom, and on a reload the top pin wins, so a short file that grows stays at its top. A **Tail** switch the user turns on makes the view follow appended text instead, as `tail -f` does. Where the switch lives, whether it is per document or per window, whether it persists in `session.json` and what turns it off are decided in the mock.

## Tree

- **Group header**: the name in semibold (the repo folder name, or the folder path with `~` for home), with a `GIT` or `DIR` badge on the right. Hovering shows the full path in a tooltip. Headers collapse with a chevron.
- Folder rows are muted and have a chevron. Compacted chains are joined with `/` (`docs/plans`).
- Document rows have a file icon. The selected row gets the selection fill and a 3 px accent bar on its left edge. A hovered or selected row shows a `×` close button on its right. Selecting a document expands its group and folders, and Up/Down move between documents, skipping headers.
- Context menu, by row: a document has Reveal in Explorer (Finder on macOS), Copy path, Close, Close group; a folder has Reveal and Copy path; a group header has Reveal, Copy path and Close group. Right-clicking an unselected document selects and opens it first; right-clicking a folder or group leaves the selection alone.

## Edge states

- **Missing document** (gone on restore or deleted while open): the row stays, greyed and struck through, with a `MISSING` tag. Selecting it shows "This file is no longer there", its full path, **Locate…** and **Remove from list**, and the note that YAMV keeps watching. If the file reappears, it reopens on its own.
- **Unreadable document** (the file exists but is locked by another program or access is denied; [mock](mocks/unreadable/index.html), option B): the row stays normal, with a 14x14 warning glyph (tooltip "Can't read this file") and no `MISSING` tag.

  Selecting it shows a warning mark, "This file can't be read", its full path, the reason ("It is in use by another program.", "Access is denied." or "It could not be read."), **Retry** (the same as Ctrl/Cmd+R), **Remove from list** and the note "YAMV tries again when the file changes." If the file goes missing, the missing pane wins.
- **Empty main window**: a dashed `md` mark, "No documents open", "Drop markdown files here, or open some. They stay open next time.", an **Open files…** button and the Ctrl/Cmd+O hint.
- **Find bar**: Ctrl/Cmd+F floats it at the document's top-right, with a field, "n of m", up/down and close. Matches are highlighted and the current one is stronger. Enter and Shift+Enter step through matches; Esc closes and clears.

## Theme settings window

- The **visual editor**:
  - **Left:** the theme list, in two sections, Built-in and Yours. Its context menu has Rename and Delete, and the Appearance choice sits at the foot of the list.
  - **Middle:** every token, grouped as in the [token table](themes.md#tokens) (Page, Chrome, Code, Syntax, Type, Layout). It has Shared / Light / Dark tabs, colour swatches with hex fields and a picker, and value fields for fonts and sizes.
  - **Right:** a live preview.
- The **preview** is a mini window drawn by Avalonia from the working copy's resolved tokens, so it follows unsaved edits at once while the window and the app keep the saved theme:
  - A 28 px top bar with the title "Release checklist.md", the muted location "yamv / docs" and a tag, LIGHT or DARK, naming the half shown. Below it a sample document (an h1, a paragraph with a link and inline code, an h2 with its rule when `h2Rule` is on, a code block with syntax colours, a quote) and a 104 px tree.
  - The Light tab shows the light half, the Dark tab the dark half and the Shared tab the half the app shows now; a one-half theme shows the half it has. An invalid theme shows no preview.
  - The sample body is a fixed 13 px and the h1 is `h1Size` × 13 px; `bodySize`, `findMatch` and `findCurrent` do not change it. A theme's own css is not previewed, and nothing goes through the page or `ThemeBridge`.
- Unsaved changes are marked with a dot.
- The window is opened from the title bar's `⋯` menu. It is owned by the window whose entry opened it, there is one at a time per process (a second open activates it), and it is closed and released before the theme bridge and the themes watcher when the app releases its main window.
- The **token editor** (the middle column; the approved mock is [theme-editor](mocks/theme-editor/index.html), [screenshot](mocks/theme-editor/index.png)):
  - One row per token, 38 px and two lines: the friendly label (such as "Selected row") at 12.5 px, and under it the token name at 10.5 px in muted mono, with the value control centred on the row. A colour row also has a swatch, which raises a pick request for the colour picker.
  - A hint line sits above the rows: "Grey values come from Shared or the defaults. ↺ removes a value from this tab." On the Shared tab it reads "Grey values come from the defaults. ↺ removes a value from this tab."
  - A grey value is inherited, from Shared or the defaults (on the Shared tab, from the defaults). A set value has a ↺ that removes it from the tab. A token with unsaved changes shows a dot.
  - When a theme has no dark half, the Dark tab keeps all three tabs and lists every token dimmed and disabled under a strip, "This theme has no dark half. In dark mode YAMV shows its light half.", with an **Add dark** button; adding the half makes the same rows editable in place. **Add light** mirrors it on a dark-only theme.
  - Enter in a field commits it, and in an inherited field it sets the shown value in that tab even when it equals the inherited one. Leaving an unchanged field by Tab or a click sets nothing. Esc restores the shown value, and on an inherited token it resets.
  - Clicking a swatch on an editable tab of a user theme opens the **colour picker**, a 244 px popup under the swatch (built-in themes open none): a saturation/value square, hue and alpha bars, the old and new colours, a hex field and the caption "Was #rrggbb. Esc puts it back."
    - Every change sets the token live: the row, its dot and the preview follow. Written hex is lowercase, `#rrggbb` when opaque, else `#rrggbbaa`.
    - In the hex field a full `#rrggbb` or `#rrggbbaa` applies as typed and `#rgb` on Enter. Invalid text shows "'{text}' is not a colour (#rgb, #rrggbb or #rrggbbaa)." and edits nothing.
    - Esc puts the token back (one inherited when the picker opened is reset, so it stays inherited) and closes. Enter or a click outside closes and keeps the value, except that a token inherited at open whose colour is back at its inherited value stays inherited.
  - A bad number or hex colour shows its error under the field. Text typed but not committed survives flipping the appearance between the Light and Dark tabs.
- Footer: **Import…**, **Export…**, **Edit as text** (opens the file in the default editor), **Revert**, **Save**. A built-in theme is read-only and has no file, so it shows **Duplicate to edit** in place of Edit as text: editing one starts with a copy in Yours. Edit as text exists only for user themes.
  - **Import…** always shows the Import notice before it copies anything: a 470 px modal on a scrim inside the window, titled "Import a theme", whose body scrolls.

    A card holds the theme chip, the bold name and a muted "by {author} · {halves} · has css|no css", the file's full path in mono, "Will be added as {Name} (n)." when the id is taken, then the file's errors and warnings. Below it come the two "A theme file can change" bullets, a safe box (css can't run code) and a warn box (css can load from the web).
  - Import is the default and focused button for a file that loads; for one that does not, the card shows the file name and no chip, Import is disabled and Cancel is focused. Esc, the close button and Cancel import nothing, and a window close while the notice is open cancels it.
- Editing a theme file outside the app re-applies it live. The window follows the selected theme's file:
  - With no unsaved edits, a changed file shows at once.
  - With unsaved edits, they stay and a **disk-change bar** spans the window under its title bar, above the three columns ([mock](mocks/theme-settings-entry/index.html), Q2 A): a refresh glyph, the bold "This theme changed on disk", **Reload** (drops the edits and loads the file) and **Keep mine** (keeps editing; the next Save writes the working copy over the file). The bar is in the accent tint over `pageBackground`, with an `accentSoft` border.
  - When the changed file no longer parses (Q2 A′), the bar takes the warning colours (`warningBackground`, `warningLine`) and a warning glyph, reads "This theme changed on disk and no longer loads:" followed by the error, and has Reload off. The row gets the warning glyph with the tooltip "This theme changed on disk and no longer loads". The window keeps the last valid copy, edits included (Q3 A), and Keep mine clears the bar.
- **Missing on disk**: when the theme in use has its file deleted or renamed outside the app, its row stays in Yours, greyed, with a muted file glyph (tooltip "Missing on disk"), and the header shows a warning `MISSING ON DISK` badge and the note "Missing on disk. Using the last loaded version." ([mock](mocks/theme-settings-entry/index.html), Q3 A).
  - The window and the document keep the last loaded version, and edits on it stay. Save writes its file again and Revert drops the edits; every other command (Rename, Delete, Export, Edit as text) is off.
  - Once another theme is in use the row goes. Another selected theme whose file is gone gives way to the theme in use.
- **Closing**: a close cancels an open Import notice or confirm, whatever its reason. With unsaved edits, any close (the user's, the owning window's or the app's shutdown) then asks Save, Discard or Cancel: Save writes and closes, Discard reverts and closes, Cancel keeps the window open. A close while that confirm is open is held.
