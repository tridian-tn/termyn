# Performance — measured against the §14 budget

The spec's §14 budget is the exit gate for phase P6. This records what was actually measured, on
what, and where Termyn stands against each target.

## Measurement machine

Not the reference profile. The spec defines the reference profile as **4-core / 16 GB Windows 11**;
these figures come from a **12-core (14 logical) Intel Core Ultra 7 155U / 31.5 GB / Windows 11 Pro
26200**, running the framework-dependent `ReadyToRun` publish of `Termyn.App.Windows`. It is a
developer laptop with a build toolchain and real-time antivirus active, which shows up in the tail
of the cold-start figures.

The account used for the cold-start and memory numbers is a real Todoist account holding **6 tasks**.
The five-thousand-task figures come from the automated budget tests (`tests/Termyn.Perf.Tests`),
which seed the reference account described in §14 into a real SQLite cache.

## Where Termyn stands

| Metric | Target | Measured | |
|---|---|---|---|
| Cold start → interactive (cache present) | p95 < 500 ms | median 454 ms, **p95 ~620–760 ms** | ✗ |
| Idle private working set | < 100 MB | **17.6 MB** (82 MB total working set) | ✓ |
| Write → visible | < 16 ms | 8–13 ms at 5,000 tasks | ✓ |
| Snapshot load + first projection, 5,000 tasks | (part of cold start) | 134–142 ms | — |
| Switching view, 5,000 tasks | one frame | 4–8 ms | ✓ |
| Search keystroke, 5,000 tasks | interactive | 0.4 ms | ✓ |
| Quick-add after hotkey | < 100 ms | not measured numerically | ? |
| Outline scroll, large list | 60 fps | not measured numerically | ? |

## Cold start — the one that misses

Twenty consecutive launches, machine otherwise idle, measured from process creation to the window
being painted and taking input:

```
396 407 409 414 417 421 432 436 438 449 454 460 472 486 501 513 534 649 756 1250
```

Median **454 ms**, p90 **649 ms**, p95 **756 ms**. A second run of twenty on the same build gave a
median of 513 ms and a p95 of 624 ms. The median is inside the budget; the p95 is not, and the tail
is dominated by variance this machine can't be made to sit still for — the slowest runs are several
times the median, which is the signature of the antivirus and the background build tooling rather
than of anything Termyn does.

**So the gate is not demonstrated met.** The median says the code is close; the tail says this
machine can't answer a p95 question.

Where the time goes, from a staged trace of a single start (before the two deferrals below):

| Stage | ms |
|---|---|
| Single-instance mutex and pipe | 13 |
| Paths and settings | 15 |
| Colour mode and `ApplicationConfiguration.Initialize` | 9 |
| `HttpClient` and the API client | 14 |
| Token read | 4 |
| **Open SQLite and load the cache** | **70** |
| **Presenter construction and first projection** | **57** |
| Global hotkey registration | 12 |
| **Tray icon** | **92** |
| `MainForm` construction | 52 |
| **`Application.Run` → first paint** | **203** |

Two of those were work the user waits on for no reason, and both have been moved off the startup
path: the tray icon is now drawn just after the window appears rather than before it, and the
quick-add window is built at the same point rather than during startup. That took the median from
513 ms to 454 ms.

What is left is mostly floor: opening SQLite pulls in its native library, and creating and painting
a themed WinForms window with a splitter, a tree and an owner-drawn list is the 203 ms. Closing the
remaining gap means attacking one of those, not trimming around them.

Note also that these are a **6-task** account. A five-thousand-task account adds the measured
134–142 ms of load and first projection on top, so the reference-profile figure would be worse
again.

## Memory — comfortable

Left idle with the window open, sampled from `\Process(Termyn)\Working Set - Private`:

- **Private working set: 17.6 MB** against a 100 MB budget.
- Private bytes 22.4 MB; total working set 82.3 MB, most of which is shared framework pages.

Flat across several minutes of idling — the figure at five minutes matched the one taken seconds
after the window appeared, so nothing is accumulating on the sync timer.

## What was made faster

Two changes during P6, both found by measuring rather than by reading:

**The model caches projected tasks.** Every publish read every task, and a publish happens on every
keystroke, write and sync — so five thousand tasks were re-parsed from JSON each time. Projections
are now held until their JSON changes. A snapshot went from **9 ms to 0.1 ms**, and a write reaching
the screen from **16 ms to 8 ms** — from the edge of the frame budget to comfortably inside it.

**Search rows are built on the first keystroke, not on every publish.** Search runs over the whole
account rather than the current view, which needs a second full projection. Most publishes happen
with the search box empty, and that projection is now built only when something is typed.

## Metrics not measured numerically

- **Quick-add after hotkey (< 100 ms).** The window is created once and hidden between uses, so a
  press positions and shows an existing form rather than building one. That is the design the budget
  asks for, but the latency itself has not been instrumented.
- **Outline scroll at 60 fps.** The list is virtual and owner-drawn with no per-row control, so the
  cost per frame is the visible rows only. Again: the right shape, not a measurement.

## Reproducing these

The automated budget checks are regression gates rather than benchmarks, and run with the rest of
the suite:

```bash
dotnet test tests/Termyn.Perf.Tests -c Release
```

They warm each path past tiered compilation before measuring — an unwarmed measurement read three
times slower than the same code in the app, which is how the write-visible figure was first
misread as 26 ms.

For the cold-start trace, set `TERMYN_STARTUP_TRACE=1` and run the published build. Each start
appends a line to `%LOCALAPPDATA%\Termyn\logs\startup.log`. It is local to the machine and off
unless the variable is set — a diagnostic, not telemetry.
