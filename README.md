# Termyn

[![CI](https://github.com/tridian-tn/termyn/actions/workflows/ci.yml/badge.svg)](https://github.com/tridian-tn/termyn/actions/workflows/ci.yml)

A fast, low-footprint Windows desktop client for [Todoist](https://todoist.com) — WinForms on
.NET 10, offline-first, keyboard-driven.

Unofficial, and not affiliated with Doist. It talks to the public Todoist API with a token you
supply.

## Build & test

```bash
dotnet build Termyn.slnx
dotnet test Termyn.slnx
```

Requires the **.NET 10 SDK**. The build is framework-dependent and `win-x64`, so running it needs
the .NET 10 Desktop Runtime — nothing bundles the runtime, which keeps the download small and lets
it be patched on its own schedule.

To produce an installer or a portable zip, see [docs/packaging.md](docs/packaging.md).

## What it does

Tasks, with the outline Todoist has: projects and sub-projects, sections, labels, saved filters,
and the smart views. Sub-tasks nest to the four levels Todoist holds — shown, created, folded away
and remembered folded.

- **Capture** with Todoist's quick-add syntax, from the window, a global hotkey, or the tray.
- **Edit** a task's name, due date, priority, labels and reminders; complete, reopen, reorder,
  indent, delete, and undo.
- **Descriptions** in markdown, written as the account stores them and drawn as they read.
- **Comments**, with file attachments.
- **Find** by searching, by a saved filter, or through the command palette.
- **Offline first.** Every change is applied locally and queued; the sync loop reconciles in the
  background and says what it couldn't land.

Saved filters are evaluated locally against as much of Todoist's grammar as a task can answer on
its own. Anything outside that — who a task is assigned to, which workspace it is in — is refused
by name rather than answered wrongly.

## Layout

The solution is split so a future native Linux/macOS client can reuse everything below the UI:

| Project | TFM | Role |
|---|---|---|
| `src/Termyn.Core` | `net10.0` | OS-agnostic domain model, sync engine, API client, platform-service interfaces |
| `src/Termyn.Presentation` | `net10.0` | UI-framework-agnostic presenters |
| `src/Termyn.Platform.Windows` | `net10.0-windows` | Windows platform services (DPAPI token store, paths) |
| `src/Termyn.App.Windows` | `net10.0-windows` | WinForms UI + composition root |
| `tests/Termyn.TestSupport` | `net10.0` | Shared test doubles |
| `tests/Termyn.Core.Tests`, `tests/Termyn.Presentation.Tests`, `tests/Termyn.Perf.Tests` | `net10.0` | xUnit tests over the portable core |
| `tests/Termyn.Platform.Windows.Tests`, `tests/Termyn.App.Windows.Tests` | `net10.0-windows` | xUnit tests over the Windows layers |

`Core` and `Presentation` target `net10.0` (not `-windows`), so the compiler prevents any
Windows-only API leaking into the portable core.

## Documents

Written where a decision took long enough to be worth explaining twice.

| Document | What it covers |
|---|---|
| [docs/packaging.md](docs/packaging.md) | How the installer and the portable zip are built and released |
| [docs/performance.md](docs/performance.md) | The start-up and rendering budgets, and what was measured against them |
| [docs/description-editor.md](docs/description-editor.md) | How the markdown panel draws and edits the same text |
| [docs/comments-pane.md](docs/comments-pane.md) | How comments and their attachments are shown |

## Licence

[MIT](LICENSE).
