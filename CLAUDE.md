# YAMV

Yet Another Markdown Viewer: C# / .NET 10 / Avalonia 12, Windows + macOS. Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first; the plan is [docs/plans/MAIN.md](docs/plans/MAIN.md).

## Commands

- Build: `gate run --log .tmp/build.log --slot heavy --timeout 600 -- dotnet build Yamv.slnx -c Release`
- Test: `gate run --log .tmp/test.log --slot heavy --timeout 600 -- dotnet test --solution Yamv.slnx -c Release --no-build`
- Format: `dotnet csharpier format .`; style: `dotnet format style Yamv.slnx --severity info --include <files>`

## Rules

- `Yamv.Core` never references Avalonia; anything testable without a window lives there.
- Test mode: stable (the full suite runs at every landing).
- The look follows `docs/design/look.md`; a UI change that departs from it gets a mock first.
