# Content width

How wide the document's text column is. It is a viewer setting and not part of the theme; theme files have no `measure` token. The reference mock is [mocks/width](mocks/width/index.html) ([screenshot](mocks/width/width.png)), option A.

## Values

- A width is a number of px from 480 up to the pane's width, or **Full**, which uses the whole pane minus its 44 px side padding.
- The slider snaps to Full within the last 5% of its travel. The readout says `760 px` or `Full`.
- When the pane is narrower than a stored px width, the column is the pane width. The stored value is never changed by a window resize.
- The app default starts at 800 px.

## Where a width comes from

- Any folder can hold a width. A document uses the width of the nearest folder at or above its own folder that has one. If no folder has one, it uses the app default.
- Folder paths are compared with the platform's path comparison (`AppPaths.PathComparison`).
- The widths are saved in `<data dir>/widths.json` (beside `session.json`): `{ "default": 800 | "full", "folders": { "<full folder path>": 760 | "full" } }`, written atomically.
- Main and viewer instances both read the file and write to it. Width is a preference, not session state. A change made in one instance reaches the other instances when they next render a document (each re-reads the file when it changes).

## The control

- A `⟷` button in the custom title bar opens a popover, which is a separate window, so it can sit over the WebView. The popover holds:
  - **Content width** and the readout;
  - the slider, labelled 480 … Full;
  - **Saved for**, a segmented control. Its first segment is the document's folder (`docs/plans`), its middle segment is the group root (`yamv (repo)`) when that is a different folder, and its last segment is **All documents** (the app default).
  - **Reset to inherited**, which removes the selected folder's own width;
  - **Clear all overrides here**, which removes every folder width at or below the group root.
- When the popover opens, the selected segment is the source of the current width: the folder it is inherited from, or All documents. Moving the slider therefore edits that source. Picking a narrower segment first creates an override there.
- The selected segment's source is shown as "Inherited from `<folder>`" when it differs from the selected segment.
- **Ctrl+[** / **Ctrl+]** (Cmd on macOS) step the current source's width by 40 px. Stepping past the pane width becomes Full, and stepping down from Full goes to the largest 40 px step below the pane width.
- The page applies the width with no reload: the host sets `--yamv-content-width` (a px value, or `none` for Full) on the page.
