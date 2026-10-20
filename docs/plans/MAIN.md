# YAMV plan

YAMV (Yet Another Markdown Viewer) is a Windows + macOS markdown viewer in C# on Avalonia 12. The approved build plan, with the interview answers and the researched stack, is [v1-build.md](./v1-build.md).

## Current focus

- [x] Scaffold the repo: configs, docs skeleton, solution, GitHub
- [ ] Mocks and the visual interview; record the choices in `docs/design/look.md` (blocks all UI work)
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
- [ ] Windows `--register` / `--unregister`
- [ ] Packaging: publish script, `make-app.sh`, icon, CI on Windows and macOS
- [ ] Docs: ARCHITECTURE filled in, README usage, CHANGELOG

## Backlog

(none)
