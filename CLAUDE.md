# YAMV

Yet Another Markdown Viewer: C# / .NET 10 / Avalonia 12, Windows + macOS. Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first; the plan is [docs/plans/MAIN.md](docs/plans/MAIN.md).

## Commands

- Build: `gate run --log .tmp/build.log --slot heavy --timeout 600 -- dotnet build Yamv.slnx -c Release`
- Test: `gate run --log .tmp/test.log --slot heavy --timeout 600 -- dotnet test --solution Yamv.slnx -c Release --no-build`
- Format: `dotnet csharpier format .`; style: `dotnet format style Yamv.slnx --severity info --include <files>`

## Rules

- `Yamv.Core` never references Avalonia; anything testable without a window lives there.
- **Nothing an agent runs may show a window on the owner's desktop or take focus.** A real `yamv.exe` (a harness, a screenshot, an end-to-end check) runs only through `pwsh -NoProfile -File tools/hidden-desktop.ps1 -- <command>` (a private Win32 desktop, copied from godot-mcp), and its capture happens inside that desktop, never through a bring-to-front. Avalonia UI tests use `Avalonia.Headless`. Page-only CSS/JS checks render in headless Chromium (`npx playwright screenshot`). A check that cannot run hidden is tagged `[Trait("Category", "Desktop")]`, excluded by default, and run only when the owner asks.
- Test mode: stable (the full suite runs at every landing).
- The look follows `docs/design/look.md`; a UI change that departs from it gets a mock first.
