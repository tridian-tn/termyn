# Termyn

A fast, low-footprint Windows desktop client for [Todoist](https://todoist.com) — WinForms on
.NET 10, offline-first, keyboard-driven.

Full spec: `d:/docs/specs/termyn-windows-todoist-client.md`.

## Build & test

```bash
dotnet build Termyn.slnx
dotnet test Termyn.slnx
```

Requires the **.NET 10 SDK**. The app is a framework-dependent build and needs the .NET 10 Desktop
Runtime at run time.

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

## Status

Task basics are in: capture with Todoist's quick-add syntax, edit, complete, reopen, delete, undo,
reorder, priorities, due dates and local search, over an offline-first sync engine with a background
sync loop. The list is still flat — projects, sections and the outline view come next.
