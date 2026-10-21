# Theme files

A YAMV theme is a text file, `<id>.yamv-theme.json`, where `<id>` is its file name without the suffix. You share a theme by sharing that one file: Import copies it into the user themes folder, and Export writes it out.

## Where themes live

- **Built-in**: `paper`, `studio` and `ink` are embedded in the app and read-only. Duplicating one writes an editable copy into the user folder.
- **User**: `<data dir>/themes/`. That is `%APPDATA%\YAMV\themes` on Windows and `~/Library/Application Support/YAMV/themes` on macOS. The folder is watched: a file that is added, edited or removed applies live.
- A user theme whose id matches a built-in one shadows it.

## Format

JSON with comments (`//`, `/* */`) and trailing commas allowed. Every field except `name` is optional.

```jsonc
{
  "$schema": "https://raw.githubusercontent.com/leftos/yamv/main/themes/yamv-theme.schema.json",
  "formatVersion": 1,
  "name": "Paper",
  "author": "YAMV",
  "description": "Warm off-white, serif body, rust accent.",

  // Shared by both halves
  "tokens": { "bodyFont": "Source Serif 4", "bodySize": 17.5, "lineHeight": 1.65 },
  "css": "",

  // Either or both. A half's tokens override the shared ones; its css is appended after the shared css
  "light": { "tokens": { "pageBackground": "#fbf8f2", "accent": "#a4542a" }, "css": "" },
  "dark":  { "tokens": { "pageBackground": "#1d1a17", "accent": "#df9a6b" } }
}
```

- A file must have at least one of `light` or `dark`.
- **Resolution** for a half: the default token values (Studio light for a light half, Studio dark for a dark one), then the file's shared `tokens`, then the half's `tokens`. The result always holds every token.
- **Errors**:
  - A file that won't parse, a colour that isn't `#rgb`, `#rrggbb` or `#rrggbbaa`, a number out of range, a font name that is empty, only whitespace or holds a control character, or a token string that isn't valid text (an unpaired surrogate such as `"\ud800"`) makes the theme invalid. It is listed with the error. Selecting it in the theme settings window shows its errors, with Delete and Rename on unless its file name gives no valid theme id; it is never applied.

    A theme file over 1 MB is listed as invalid without being read, and a file that disappears while the folder is read is skipped.
  - An unknown token name is a warning; the token is ignored.
  - An unknown field (`"Light"`, `"token"`) is a warning; the field is ignored. `$schema` is allowed.
  - A `formatVersion` other than `1` is a warning, and so is one that isn't an integer (`2.0`, `"2"`); the file still loads.
  - A user file with a built-in theme's id replaces the built-in when it is valid. When it is invalid, the built-in stays listed and the invalid file is listed beside it with its errors, so two entries share the id.
  - Import never replaces a built-in or an existing user theme: a file whose id is taken is imported as `<id>-2`, `<id>-3`, and so on, and its name becomes `<Name> (2)`, `<Name> (3)` (only the `name` value in the copied file is rewritten). The imported theme is selected and applied.
  - Export writes a user theme's file bytes exactly, and a built-in theme's embedded text, to a file the user picks, suggesting `<id>.yamv-theme.json`. With unsaved edits it first asks Save, Discard or Cancel.
  - Duplicate writes `<id>-2` named `<Name> (copy)`, then `<id>-3` named `<Name> (copy 2)`.
  - Rename changes the theme's name and the file follows it: the file name is the name lower-cased, with each run of characters other than ASCII letters and digits turned into `-`, cut to 64 characters (`theme` when nothing is left). A taken or device name gets the `-2` suffix. A file already named for the new name, or `<name>-<n>`, keeps its name. If the file cannot be moved, the theme keeps its old id with the new name.
  - Save of an existing file changes only the edited values in the file's text, so comments, key order, unknown fields and a BOM survive. It is refused ("could not be updated in place") when the file on disk is not a valid theme, when an edited key appears twice in it, or when the patched text would not read back as the saved theme.
  - Delete moves the file to the Recycle Bin (Trash on macOS) rather than erasing it.
  - A user file whose name gives no valid theme id is listed as invalid on every platform without being read, with a "rename the file" error. The theme settings window lists it and shows that error, with Rename and Delete off.

    A Windows device name (`con`, `prn`, `aux`, `nul`, `com1`, `lpt1`…, in any case, with or without a further extension, such as `con.yamv-theme.json`) gives `"<file name>" uses a name Windows reserves for a device; rename the file.`, and any other — an empty name, `.`, `..`, a slash, a backslash or a control character — gives `The file name '<file name>' gives no usable theme id; rename it to <name>.yamv-theme.json.`
  - The app never falls back silently. If the selected theme turns invalid, the window keeps the last valid version and shows the error in the theme settings window. The same holds when the file is deleted or renamed, and when a user file overriding a built-in id turns invalid: the built-in does not replace it. A changed valid file is applied to every window.
  - When the selected theme's file changes on disk while unsaved edits are open, the edits stay and a bar offers Reload (drops them) or Keep mine (the next Save overwrites the file). When the changed file no longer parses, the bar shows the error with Reload off, and the window keeps the last valid copy, edits included.
  - When the theme in use has its file deleted or renamed outside the app, its row stays, greyed and marked "Missing on disk", in its last loaded version with any edits. Save writes the file again; Rename, Delete, Export and Edit as text are off.
- `css` is appended to the document page after YAMV's own stylesheet. It styles the document only, not the app chrome. Scripts can't run in the page (the page's Content-Security-Policy blocks them), so `css` can't run code.

  Theme css can make the page load images from the web (`url(https://…)` in CSS), which can reveal that you opened a document and which links it holds. Import a theme from someone you trust, as you would a browser extension's styles. On every Import the app shows a modal, "Import a theme", with a card for the file (name, author, which halves it has, whether it has css, the file's full path, and its errors and warnings) and this warning. A file that cannot be imported shows its file name and no chip, and only Cancel works.

## Tokens

Each token is also a CSS custom property on the document page, `--yamv-<kebab-case-name>` (`pageBackground` → `--yamv-page-background`). The app chrome (title bar, tree, find bar, dialogs) reads the same values.

| Group | Token | Type | Used for |
|---|---|---|---|
| Page | `pageBackground` | colour | document background |
| Page | `text` | colour | body text |
| Page | `textSecondary` | colour | tree rows, secondary text |
| Page | `textMuted` | colour | captions, folder rows, table headers, blockquotes |
| Page | `heading` | colour | h1–h6 |
| Page | `accent` | colour | links, selection bar, checkboxes, primary buttons |
| Page | `accentSoft` | colour | link underline, focus ring, badge border |
| Page | `line` | colour | hairlines, table rules, borders |
| Page | `quote` | colour | blockquote bar |
| Chrome | `chromeBackground` | colour | title bar, dialog footers |
| Chrome | `sidebarBackground` | colour | tree |
| Chrome | `selectionBackground` | colour | selected tree row |
| Chrome | `selectionText` | colour | selected tree row text |
| Chrome | `hoverBackground` | colour | hovered tree row |
| Chrome | `warning` | colour | `MISSING` tag text |
| Chrome | `warningBackground` | colour | `MISSING` tag fill |
| Chrome | `warningLine` | colour | `MISSING` tag border |
| Code | `codeBackground` | colour | inline and block code, the Import notice's safe box |
| Code | `codeLine` | colour | code block border |
| Code | `findMatch` | colour | find highlight |
| Code | `findCurrent` | colour | current find highlight |
| Syntax | `syntaxKeyword` | colour | keywords |
| Syntax | `syntaxString` | colour | strings |
| Syntax | `syntaxComment` | colour | comments |
| Syntax | `syntaxType` | colour | types, titles, functions |
| Syntax | `syntaxNumber` | colour | numbers, literals |
| Type | `uiFont` | font family | app chrome |
| Type | `bodyFont` | font family | document body |
| Type | `headingFont` | font family | headings |
| Type | `monoFont` | font family | code |
| Layout | `bodySize` | number, px, 10–32 | body font size |
| Layout | `lineHeight` | number, 1.0–2.5 | body line height |
| Layout | `h1Size` | number, em, 1.0–4.0 | h1 size |
| Layout | `headingWeight` | number, 100–900 | h1/h2 weight |
| Layout | `h2Rule` | bool | rule under h2 |

A font family is tried as named, then falls back to the bundled family for its role (Inter for UI, body and headings; JetBrains Mono for mono). Fonts are not embedded in theme files.

The Ink family's heading markers (`# `) and upper-case table headers are written in its `css`, which makes it a worked example of the field.
