# Content width

How wide the document's text column is. It is a viewer setting and not part of the theme; theme files have no `measure` token. The reference mock is [mocks/width-popover](mocks/width-popover/index.html) ([screenshot](mocks/width-popover/width-popover.png)).

## Values

- A width is the text measure: a number of px from 480 up to the pane's width minus its 88 px of side padding, or **Full**. A stored 760 draws 760 px text lines inside the column's 44 px side paddings (an 848 px column box), and Full fills the pane minus those paddings.
- The slider snaps to Full within the last 5% of its travel. The readout says `760 px` or `Full`.
- A stored px width wider than the window's text space is shown as itself: the readout says `800 px` with a muted second line "wider than this window", and the knob is pinned at the right end as a dashed ring with a centre dot over a hatched cap on the last 14% of the track. Opening and closing the popover keeps the stored width; any drag or key clears the pin and works as above, so the end saves Full. The popover does not follow a window resize while it is open.
- The popover's arrow and card cast one shadow together and share one 1 px `line` rim, with no seam where the arrow meets the card.
- When the pane is narrower than a stored px width plus the side padding, the column is the pane width. The stored value is never changed by a window resize.
- The app default starts at 800 px, which is also the page's fallback text measure when the host has set none.

## Where a width comes from

- Any folder can hold a width. A document uses the width of the nearest folder at or above its own folder that has one. If no folder has one, it uses the app default. The walk up ends at the file-system root; the group root does not bound it, and bounds only **Clear all overrides here**.
- Folder paths are compared with the platform's path comparison (`AppPaths.PathComparison`).
- The widths are saved in `<data dir>/widths.json` (beside `session.json`): `{ "default": 800 | "full", "folders": { "<full folder path>": 760 | "full" } }`, written atomically.
- Main and viewer instances both read the file and write to it. Width is a preference, not session state. A change made in one instance reaches the other instances when they next render a document (each re-reads the file when it changes).

## The control

- A `⟷` button in the custom title bar (accessible name "Content width", no tooltip) opens a popover: a native Avalonia Popup anchored to it, which is a separate window, so it can sit over the WebView. The popover holds:
  - **Content width** and the readout;
  - the slider, labelled 480 … Full; its arrow, Page Up/Down, Home and End keys (with or without modifiers) move it on the 40 px grid;
  - **Saved for**, a segmented control of two to five segments, deepest folder first. They are the document's folder; the folder the width comes from, when it lies between the document's folder and the group root; the group root (`yamv (repo)`, marked when it is a git work tree), hidden when it is the document's folder; the folder the width comes from, when it lies above the group root; and last **All documents** (the app default). A folder segment below the group root is labelled by its path below the segment above it (`plans`, or `docs/plans`), and one above the group root by its name. The segments have equal widths while every label fits an equal share; otherwise each is its label's width plus 6 px a side plus an equal share of the rest.
  - **Reset to inherited**, which removes the selected folder's own width; it is enabled only when the selected segment is a folder that holds a width of its own;
  - **Clear all overrides here**, which removes every folder width at or below the group root. It is enabled only when such a width exists, and asks first, inline: the link is replaced by "Clear N saved width(s) under {group root}?" with **Clear** and **Cancel** in link style. Focus moves to Cancel, and back to the link (or to the selected segment when the link becomes disabled) when the question ends.

    The focus ring stays with the focus when the keyboard gave it, and a pointer's focus stays ringless.
- The popover closes on Esc, on **⟷** again, on a click outside it (light dismiss) and when the window deactivates. Focus returns to the document only on Esc or **⟷**. Of the window's shortcuts only **Ctrl+[** / **Ctrl+]** are forwarded from the popover to the window.
- The group root is the document's group in the tree. A viewer window with no tree uses the document's own folder as its group root, so Saved for shows that folder and All documents; a viewer window that shows a tree uses the tree's group root.
- The slider is disabled, with the caption "The window is narrower than 480 px", when the pane leaves less than 480 px of text space.
- Moving the slider changes the page live and saves nothing; the width is saved when the drag ends, for the segment shown selected at the release (a segment pick whose read has not landed is not yet the selection; a Reset or Clear all still queued at the release does not move the save). The save skips the write when the store, read at save time and not the popover's cached width, already holds that width for the segment, so a width another window changed meanwhile is overwritten by the drag. After a Reset during a drag on a middle folder, the release leaves that folder selected with the dragged width, **Reset to inherited** enabled and **Clear all overrides here** counting it. A drag on a segment that cannot reach the document (a folder nearer the document holds its own width, so the document would not change) leaves the page alone and still saves the segment's width when it ends.
- A save that fails springs the slider back to the stored width, gives the page the stored width again, and shows "Could not save the width; see the log." in the popover's foot for 4 s. A save that fails after the popover has closed shows a toast ([look.md](look.md)), so the failure is never only a log line.
- The popover is for one document: when the window's document changes or stops being shown, it goes stale and does nothing more, and the page keeps the stored width. Every width change the window makes (slider, Reset, Clear all, **Ctrl+[** and **Ctrl+]**) runs in issue order on one queue, so the later action wins in the store and on the page. An older popover's re-send of the stored width yields while a newer popover holds the slider.
- When the popover opens, the selected segment is the source of the current width: the folder it is inherited from, or All documents. Moving the slider therefore edits that source. Picking a narrower segment first creates an override there.
- The selected segment's source is shown as "Inherited from `<folder>`" when it differs from the selected segment.
- **Ctrl+[** / **Ctrl+]** (Cmd on macOS) match the physical keys in the US `[` / `]` positions only when the keyboard layout prints no letter on that key, with Ctrl (Cmd) alone (no Shift, no Alt). Where the layout prints a letter there (Bépo prints `w` on the US `]` key), that letter's own shortcut runs (Ctrl+W closes the document) and the width step has no key on that layout, on both the window path and the page path; the popover slider still sets the width. The page forwards the keys it matches to the host as `[` / `]`, and a `[` or `]` typed from any other physical key is not forwarded as a shortcut. They step the current source's width on a 40 px grid that starts at 480. From a width off the grid, one press snaps to the next grid value (from 763 px, up goes to 800 and down to 760); from a grid value it moves by 40. Stepping down stops at 480. Stepping past the pane width becomes Full, and stepping down from Full goes to the largest grid value below the pane width.
- The step compares against the text space, the pane width minus the 88 px of side padding. Steps write the width's source (the folder it is inherited from, or the app default) to `widths.json`; two quick presses give two steps, and a failed write leaves the page at its width.
- The page applies the width with no reload: the host sets `--yamv-content-width` (a px value, or `none` for Full) on the page.
- Every document, in the main window and in viewer windows, reaches the page with its stored width: the host resolves it from `widths.json` while it reads the document, and the page applies it only once the outgoing document is hidden, so a document never shows at the previous one's width.

## A width that cannot reach the document

- Dragging the slider on a segment whose width cannot reach the shown document (a narrower folder nearer the document holds its own width, so the document would not change) previews the width on a temporary example document, shown in place of the page, with a small toast ([look.md](look.md)) explaining why the change will not apply to the current document. The example document, when it appears and goes back, and the toast wording and place are decided in a mock first; this is not built yet.
