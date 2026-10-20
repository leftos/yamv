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

  // Shared by both variants
  "tokens": { "bodyFont": "Source Serif 4", "bodySize": 17.5, "lineHeight": 1.65, "measure": 720 },
  "css": "",

  // Either or both. A variant's tokens override the shared ones; its css is appended after the shared css
  "light": { "tokens": { "pageBackground": "#fbf8f2", "accent": "#a4542a" }, "css": "" },
  "dark":  { "tokens": { "pageBackground": "#1d1a17", "accent": "#df9a6b" } }
}
```

- A file must have at least one of `light` or `dark`.
- **Resolution** for a variant: the default token values (Studio light for a light variant, Studio dark for a dark one), then the file's shared `tokens`, then the variant's `tokens`. The result always holds every token.
- **Errors**:
  - A file that won't parse, a colour that isn't `#rgb`, `#rrggbb` or `#rrggbbaa`, or a number out of range makes the theme invalid. It is listed with the error and can't be selected.
  - An unknown token name is a warning; the token is ignored.
  - The app never falls back silently. If the selected theme turns invalid, the window keeps the last valid version and shows the error in the theme settings window.
- `css` is appended to the document page after YAMV's own stylesheet. It styles the document only, not the app chrome. Scripts can't run in the page (the page's Content-Security-Policy blocks them), so `css` can't run code.

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
| Code | `codeBackground` | colour | inline and block code |
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
| Layout | `measure` | number, px, 400–1600 | max text width |
| Layout | `h1Size` | number, em, 1.0–4.0 | h1 size |
| Layout | `headingWeight` | number, 100–900 | h1/h2 weight |
| Layout | `h2Rule` | bool | rule under h2 |

A font family is tried as named, then falls back to the bundled family for its role (Inter for UI, body and headings; JetBrains Mono for mono). Fonts are not embedded in theme files.

The Ink family's heading markers (`# `) and upper-case table headers are written in its `css`, which makes it a worked example of the field.
