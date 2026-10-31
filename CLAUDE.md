# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# YAMV

Yet Another Markdown Viewer: C# / .NET 10 / Avalonia 12, Windows + macOS. Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first.

## Plan

Every task is a Linear issue in team YAMV; [docs/plans/MAIN.md](docs/plans/MAIN.md) is a generated snapshot, changed in Linear and never by hand. A steer mid-task gets an **add** first. The rules are in `~/.claude/docs/plan-operations.md`; the nextup profile is `.claude/skills/yamv-nextup/SKILL.md`.

## Commands

Every busy command goes through `pwsh -NoProfile -File tools/gate.ps1`, written `gate.ps1` below.

- Build: `gate.ps1 run --log .tmp/build.log --slot heavy --timeout 600 -- dotnet build Yamv.slnx -c Release` (warnings are errors, analyzers at `latest-recommended`, set in `Directory.Build.props`).
- Test: `gate.ps1 run --log .tmp/test.log --slot heavy --timeout 600 -- dotnet test --solution Yamv.slnx -c Release --no-build`. Tests are xUnit v3 on Microsoft.Testing.Platform (`global.json`).
- One test class or method: `gate.ps1 run --log .tmp/test-one.log --slot light --timeout 300 -- dotnet test --project tests/Yamv.Core.Tests -c Release --no-build --filter-class "*TitleTextTests"` (or `--filter-method "*Name*"`).
- App tests (headless Avalonia) are their own project; one class: `gate.ps1 run --log .tmp/test-one.log --slot light --timeout 300 -- dotnet test --project tests/Yamv.App.Tests -c Release --no-build --filter-class "*ThemeBridgeTests"`.
- Page check (headless Chromium against a stubbed host): in `tools/dev/page-check/`, `npm install` then `npm run check`.
- Screenshot of the real app: `pwsh -NoProfile -File tools/hidden-desktop.ps1 -- pwsh -NoProfile -File tools/dev/run-hidden.ps1 -ArgLine '--theme dark' -Out .tmp/shot.png` (the dev harness). `-App` runs the real main window instead, `-Session <file>` seeds its `session.json` (documents and layout), `-Settings <file>` seeds its `settings.json` (theme and Appearance), and `-DeleteAfterShown <path>` deletes a file after the window shows (all three need `-App`).

  `-ThemeSettings` runs the theme settings window instead (not with `-App`), and `-Themes <folder>` copies that folder's `*.yamv-theme.json` files into its themes folder first; `-ImportFile <path>` (with `-ThemeSettings`) opens the Import notice for that theme file once the window has shown (`YAMV_DEV_IMPORT_FILE`).

  `-Widths <file>` seeds the `widths.json` (with `-App`), `-WidthPopover` (with `-App`) opens the content-width popover once the window has shown and paints it over the capture, and `-WidthPopoverStep clearall|folder` (with `-WidthPopover`) asks Clear all or selects the document's folder segment (`YAMV_DEV_WIDTH_POPOVER`); `-Toasts` (with `-App`, not with `-WidthPopover`) shows three sample toasts once the window has shown, one with inline code and one with a ×3 count, and paints their popup over the capture (`YAMV_DEV_TOASTS`); `-ThemeSettingsEntry menu|open` (with `-App`, not with `-WidthPopover`, `-Toasts` or `-DropOverlay`) opens the title bar's ⋯ menu and paints it over the capture, or asks for the theme settings window as the menu's item does and captures that window (`YAMV_DEV_THEME_SETTINGS_ENTRY`), through `tools/dev/capture-window.ps1 -Title <text>`, which captures the process's visible window whose title contains the text instead of its largest one; `tools/dev/capture-window.ps1 -WithPopups` paints a process's popups over the capture, but its PrintWindow draws a popup's transparent margin black.

  `-DropOverlay ok|no` (with `-App`, not with `-WidthPopover`, `-Toasts` or `-ThemeSettingsEntry`) shows the drop overlay's "Drop to open" form or its grey refusal once the window has shown, and paints it over the capture (`YAMV_DEV_DROP_OVERLAY`, read only when `YAMV_DATA_DIR` is set).

  `-ConfirmCard` (with `-App`, not with `-WidthPopover`, `-Toasts`, `-ThemeSettingsEntry` or `-DropOverlay`) opens the confirm card for a sample `setup.ps1` once the window has shown and paints it over the capture (`YAMV_DEV_CONFIRM_CARD=1`, read only when `YAMV_DATA_DIR` is set); the hidden capture draws the card's scrim over black.

  A real OLE drag works on the hidden desktop: the drag is driven with posted mouse messages and a `WH_GETMESSAGE` hook, so a drop can be tested without the owner's desktop.

  Every run uses a fresh data folder under `.tmp\yamv-data` (`YAMV_DATA_DIR`), never `%APPDATA%\YAMV`.
- Format: `dotnet csharpier format .`; style: `dotnet format style Yamv.slnx --severity info --include <files>`.
- Commit: the prek pre-commit hooks run format and style, so commit through `gate.ps1 run --log .tmp/commit.log --slot light --timeout 1200 -- git commit -F .tmp/msg-<slug>.txt`. The pre-push hook runs a Release build, so a push goes through `gate.ps1 run --log .tmp/push.log --slot heavy --timeout 1200 -- git push`.

## Rules

- `Yamv.Core` never references Avalonia; anything testable without a window lives there.
- The document is drawn by a web page (`src/Yamv.App/Web/`) inside Avalonia's `NativeWebView`, loaded over `file://` from the asset cache. The page and `Controls/DocumentView.cs` talk by message name, so a rename changes both sides; the full list and the other cross-file couplings are under Integration Footguns in `docs/ARCHITECTURE.md`.
- **Nothing an agent runs may show a window on the owner's desktop or take focus.** A real `yamv.exe` (a harness, a screenshot, an end-to-end check) runs only through `pwsh -NoProfile -File tools/hidden-desktop.ps1 -- <command>` (a private Win32 desktop, copied from godot-mcp), and its capture happens inside that desktop, never through a bring-to-front.

  Avalonia UI tests use `Avalonia.Headless`. Page-only CSS/JS checks render in headless Chromium (`npx playwright screenshot`). A check that cannot run hidden is tagged `[Trait("Category", "Desktop")]`, excluded by default (the test project's `DesktopTestFilter` property), and run only when the owner asks, with `-p:DesktopTestFilter='--filter-trait "Category=Desktop"'`.
- Test mode: stable (the full suite runs at every landing).
- The look follows `docs/design/look.md`; a UI change that departs from it gets a mock first.
