# YAMV — Yet Another Markdown Viewer

A quiet markdown viewer for Windows and macOS.

- `yamv` opens the main window with the documents you had open last time, grouped on the right by git repo or folder.
- `yamv a.md b.md` opens a separate viewer window with just those files. It remembers nothing.

Built with C# on .NET 10 and Avalonia 12. Docs start at [docs/README.md](docs/README.md).

## Build

```
dotnet build Yamv.slnx -c Release
dotnet test --solution Yamv.slnx -c Release --no-build
```

## Building and packaging

`scripts/publish.ps1` publishes a self-contained build into `publish/<rid>`, from any folder:

```
pwsh scripts/publish.ps1 -Rid win-x64     # publish/win-x64/yamv.exe, one file
pwsh scripts/publish.ps1 -Rid osx-arm64   # publish/osx-arm64/, a folder for make-app.sh
pwsh scripts/publish.ps1 -Rid osx-x64     # the same for Intel Macs
```

On a Mac, `scripts/make-app.sh` turns a published macOS folder into `YAMV.app` and signs it ad hoc:

```
bash scripts/make-app.sh publish/osx-arm64 dist   # dist/YAMV.app
```

CI (`.github/workflows/ci.yml`) builds and tests on Windows and macOS for every push to `main` and every pull request, and uploads two artifacts: `yamv-win-x64.exe` and `YAMV-osx-arm64.app.zip`. GitHub downloads `yamv-win-x64.exe` as a zip holding `yamv.exe`.

The CI artifacts are development builds: they do not open a usable window yet, since the main window is still the development harness.

A downloaded `YAMV.app` is ad-hoc signed, not notarized, so macOS quarantines it. Clear the quarantine before the first open:

```
xattr -dr com.apple.quarantine YAMV.app
```
