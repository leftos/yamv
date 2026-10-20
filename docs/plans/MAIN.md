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
- [ ] Windows `--register` / `--unregister`
- [ ] Packaging: publish script, `make-app.sh`, icon, CI on Windows and macOS
- [ ] Docs: ARCHITECTURE filled in, README usage, CHANGELOG

## Backlog

(none)
