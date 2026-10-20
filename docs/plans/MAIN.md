# YAMV plan

YAMV (Yet Another Markdown Viewer) is a Windows + macOS markdown viewer in C# on Avalonia 12. The approved build plan, with the interview answers and the researched stack, is [v1-build.md](./v1-build.md).

## Current focus

- [x] Scaffold the repo: configs, docs skeleton, solution, GitHub
- [x] Mocks and the visual interview; choices recorded in [look.md](../design/look.md), theme format in [themes.md](../design/themes.md)
- [ ] Core: theming (theme file model, resolution, CSS generation, catalog of built-in and user themes, the three built-in families)
- [ ] Core: `GitRootFinder`
- [ ] Core: `DocumentGrouper`
- [ ] Core: `SessionStore`
- [ ] Core: `InstanceCoordinator`
- [ ] Core: `LaunchOptions`
- [ ] Core: `MarkdownRenderer`

## Next up

- [ ] App shell: WebView spike (local images), template/css/js, the window with its tree, built to the chosen mock
- [ ] Launch modes: main vs viewer, activation of the main instance, macOS file activation
- [ ] Extras: live reload, find in page, drag-drop and File > Open, in-app `.md` links
- [ ] Themes: all six mock variants (Paper, Studio, Ink × light/dark) are selectable, plus "Match system". Each theme is a shareable JSON file in a user themes folder, live-reloaded on edit, and can be edited in a Theme settings window (mock first) with Import/Export (requested by the user mid-build)
- [ ] Content width (user request): a slider setting how wide the document column is, ending at full pane width. It is remembered per repo/folder group, with one app-wide default (design interview and mock pending)
- [ ] Windows `--register` / `--unregister`
- [ ] Packaging: publish script, `make-app.sh`, icon, CI on Windows and macOS
- [ ] Docs: ARCHITECTURE filled in, README usage, CHANGELOG

## Backlog

- [ ] macOS: put the instance pipe at an absolute path under the data folder (`.NET` accepts an absolute pipe name on Unix, `PipeStream.Unix.cs:199-210`), so a terminal launch with a user-set `TMPDIR` still finds a main instance started from Finder (`src/Yamv.Core/Instance/InstanceCoordinator.cs` `DefaultPipeName`; review of 20caedf)
- [ ] Windows: hash the account SID instead of `Environment.UserName` into the pipe name, so two accounts with the same short name (local vs domain) don't collide on server creation (`InstanceCoordinator.DefaultPipeName`; review of 20caedf)
- [ ] Tests: fold the three fake logs (`CaptureLog` in GitRootFinderTests, `RecordingAppLog`, the instance tests' recorder) into one thread-safe recorder in `tests/Yamv.Core.Tests/` (review of 0b8431b–20caedf)
