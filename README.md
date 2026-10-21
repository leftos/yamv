# YAMV — Yet Another Markdown Viewer

A quiet markdown viewer for Windows and macOS.

- `yamv` opens the main window with the documents you had open last time, grouped on the right by git repo or folder.
- `yamv a.md b.md` opens a separate viewer window with just those files. It remembers nothing.
- Ctrl+O (Cmd+O on macOS), or the empty window's **Open files…** button, picks one or more markdown or text files and opens them; a file that is already open is selected instead of added twice.
- Right-click a document, folder or group in the tree for Reveal in Explorer (Finder on macOS), Copy path, and for documents and groups, Close and Close group.
- A missing document's **Locate…** picks the file that replaces it, in the same place in the tree.
- A document whose file exists but cannot be read (in use by another program, or access denied) shows a can't-read pane with the reason, **Retry** (Ctrl+R, Cmd+R on macOS) and **Remove from list**; YAMV retries when the file changes, and its tree row stays normal with a warning mark.

## Command line

- `yamv --help`, `-h` or `/?` prints the usage to standard output and exits 0.
- An unknown option, an option combined with other arguments, or a file that does not exist prints its message to standard error and exits 2.
- `yamv --register` (Windows) adds YAMV to Explorer's Open with for `.md` and `.markdown`, for the `yamv.exe` you run it from, and never takes over the default handler. It prints `Registered YAMV for .md and .markdown in Open with (<path>).`, or `YAMV is already registered for .md and .markdown (<path>).` when nothing changed, and exits 0. Run from a program that is not `yamv.exe`, it prints `Not registering: <path> is not yamv.exe. Run --register from the installed yamv.exe.` and exits 1.
- `yamv --unregister` removes the entries that point at the `yamv.exe` you run it from and leaves any that point at another program. It prints `Removed YAMV from Open with for .md and .markdown.`, or `Nothing to remove.`, and exits 0, with a note after the line when it left an entry in place. A registry failure prints `Could not register: <message>` or `Could not unregister: <message>` to standard error and exits 1.
- On macOS, `--register` and `--unregister` print a note to standard error and exit 2: Finder's Open With lists YAMV from its app bundle.

`yamv.exe` is a windowed program, so a terminal does not wait for it: its output arrives after the prompt returns, and its exit code shows only through `start /wait yamv --register` in cmd or `Start-Process -Wait -PassThru yamv --register` in PowerShell.

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

The CI artifacts are development builds: the main window shows the restored documents, but nothing is saved between runs yet.

A downloaded `YAMV.app` is ad-hoc signed, not notarized, so macOS quarantines it. Clear the quarantine before the first open:

```
xattr -dr com.apple.quarantine YAMV.app
```
