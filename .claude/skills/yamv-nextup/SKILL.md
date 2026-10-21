---
name: yamv-nextup
description: Profile for the user-level `nextup` skill in the yamv repo — loaded by `nextup` at its step 0 for this project's plan convention, agents, gates, docs map and landing path. Not a loop of its own; invoke `/nextup`.
---

# yamv profile for `nextup`

The generic loop is the user-level `nextup` skill; this file supplies only what is yamv-specific.

siblings: none
linear: yamv
test mode: stable

## Plan and tracker

- The plan lives in Linear: every task is a Linear issue in team YAMV, per `~/.claude/docs/plan-operations.md`; `docs/plans/MAIN.md` is its generated snapshot, never edited by hand. Projects are worked in Linear's project order (Core, App shell, Ship v1, then Backlog); an item no project fits goes to `Backlog`. The v1 design is [v1-build.md](../../../docs/plans/v1-build.md); every brief reads its step for what it touches, and the `docs/design/` spec the issue links.
- Pre-loop hooks: none.
- An item **land**s after its commit.
- A steer or a finding the item does not fix gets an **add**, in the project whose files it shares, else in `Backlog`.
- Tracker: **triage** as plan-operations says, each issue placed in the project that shares its files, else in `Backlog`.
- Pull requests: `gh pr list --repo leftos/yamv --state open --json number,title,headRefName`; any open PR is triaged as plan-operations says.

## Rulings every brief carries

- **`Yamv.Core` never references Avalonia**; anything testable without a window lives there and is tested there.
- **No window on the owner's desktop.** A real `yamv.exe` runs only through `pwsh -NoProfile -File tools/hidden-desktop.ps1 -- <command>`, and its capture happens inside that desktop; Avalonia UI tests use `Avalonia.Headless`; page-only CSS/JS checks render in headless Chromium. A check that cannot run hidden is tagged `[Trait("Category", "Desktop")]` and runs only when the owner asks (`CLAUDE.md`).
- **The look follows `docs/design/look.md`**; a UI change that departs from it gets a mock first.
- **Page content is trusted only as far as the CSP allows**: `script-src 'self'`, no inline script, no eval; document HTML never reaches script.

## Agents and gates

- Explore: `Explore`. Design second opinion and invariant passes: `oracle`.
- Implementer: tier 1 and tier 2 briefs go to DeepSeek through `dispatch`, as `plan-execution` routes them; the Opus `implementer` through `Agent` takes tier 3 briefs, concurrency work (`src/Yamv.Core/Instance/`), and any brief with an open design call.
- Reviewers: `oracle` for the first review of any item touching concurrency, file I/O or the host and page protocol; `code-review` otherwise and for re-reviews.
- Gates, from the repo root (or `--cd <worktree root>` for a worktree):
  - Build: `gate run --log .tmp/build.log --slot heavy --timeout 600 -- dotnet build Yamv.slnx -c Release`
  - Test: `gate run --log .tmp/test.log --slot heavy --timeout 600 -- dotnet test --solution Yamv.slnx -c Release --no-build`
  - Format: `dotnet csharpier format .`; style: `dotnet format style Yamv.slnx --severity info --include <files>`
- Needs the user: anything on the Mac (Finder open, Dock reopen, WKWebView images), and Explorer's Open with after `--register`.
- Parent-side gate: `git status --short` in the worktree and the main checkout.

## Traps

- A routed gate run brings back only changed files, and a `--no-build` test after a routed build drops `--no-build` and builds on the builder.
- `tools/dev/run-hidden.ps1` gives each hidden run its own WebView2 user-data folder under `.tmp/webview2/` (through `WEBVIEW2_USER_DATA_FOLDER`); any other hidden launch must set that variable too, or it joins the owner's `%LOCALAPPDATA%\YAMV\webview2` browser process on another desktop. The per-run folders are never pruned.
- System.Text.Json source generation: members absent from the JSON arrive as their type defaults, not as the record initializers (`SessionStore.Normalize` repairs them).

## Concurrency

- Ceiling: **three** implementers, each in its own worktree, `git worktree add ../yamv.wt/<slug> -b <slug> main`. Shared files that serialize two items: `src/Yamv.App/Views/MainWindow.axaml(.cs)`, `src/Yamv.App/Program.cs`, `src/Yamv.Core/Yamv.Core.csproj`, `src/Yamv.App/Yamv.App.csproj`, `Directory.Packages.props`.

## Docs map

| What changed | Owning docs |
|---|---|
| A behaviour, setting or command-line option the user sees | `README.md`, `CHANGELOG.md` |
| Any file under `src/` (a type added, moved or given a new job) | `docs/ARCHITECTURE.md` Layers and Task Index |
| The host and page protocol (`src/Yamv.App/Web/` against `DocumentView`) | `docs/ARCHITECTURE.md` Integration Footguns |
| A test file or fixture added or moved | `docs/ARCHITECTURE.md` Test locations |
| The look, the theme format or content width | `docs/design/look.md`, `themes.md`, `content-width.md` |
| A term used in a project-specific sense | `docs/glossary.md` |
| An item finished | **land** its issue after the commit |

## Landing

- An item built in a worktree: copy or commit its change onto `main` in the main checkout, build and run the full suite there through the gate, commit one item per commit with a `Refs: YAMV-<n>` trailer, `git push`, then remove the worktree and branch per the user-level `nextup` §4 step 6.
- A small change (docs, plan, config): commit on `main` and `git push`.
- Commit messages: ≤4-char type tag, imperative, ≤72-char subject, the session's attribution trailers.
