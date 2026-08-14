# The notes editor

A task's description is the one place in Termyn where somebody writes more than a line. This records
what the panel is, why the two-pane arrangement is being taken out, what was considered in its place,
and what would change the answer.

## What it is today

The notes panel sits under the outline and is off until you ask for it. Turned on, it splits again:
the markdown as it is written on the left, the same markdown rendered on the right.

```
┌──────────────────────────────────────────────────────────┐
│  OUTLINE                                                  │
├───────────────────────────────┬──────────────────────────┤
│  - [ ] **call** the plumber   │  ☐ call the plumber       │
│  see <https://example.com>    │  see example.com          │
│  (monospace TextBox, typed)   │  (MarkdownView, read)     │
└───────────────────────────────┴──────────────────────────┘
```

- `_notes` — a monospace `TextBox` holding the markdown the account stores.
- `_rendered` — `MarkdownView`, a read-only `RichTextBox` drawn from the markdown by Markdig. Links
  are clickable, and hovering one says where it goes.
- `DescriptionDraft` — which task the box is on and what it was opened with, so a sync arriving
  mid-sentence can't write over the typing and closing an untouched box can't push a stale copy back.
- Two idle timers: 300 ms before re-rendering, 5 s before saving.

The renderer and the draft are the good parts and are not in question. The arrangement is.

## Why it's changing

Four things compound, and they compound in the same direction.

**It's the same text twice.** The left half is set in a fixed-width face for no reason except that
without it the two halves of a description with no formatting in it are identical, and the panel
looks like it has done nothing. That is a workaround for the arrangement, not a feature of it.

**Both halves are too small.** The panel opens 180 px tall — about nine lines — and the vertical
split takes 320 px off a pane that was already short. Neither half is a comfortable place to read,
and neither is a comfortable place to write.

**The halves have different powers.** Only the left one takes typing. Only the right one has
clickable links and hover targets. So you work in one and look at the other, and the panel is at its
worst exactly when the notes have something in them worth following.

**It costs two toggles and no keys.** Show notes and show formatted notes are both menu-only, in an
app whose whole argument is the keyboard. The second toggle exists only because the first produces a
split that some people will immediately want back.

None of this is in the spec. §8 describes the sidebar and the outline and says nothing about a notes
editor, so nothing here is holding up a written requirement.

## What has to hold

| Constraint | Where from | What it rules on |
|---|---|---|
| MIT-clean | §8.3 — ObjectListView was rejected over GPLv3 | Any dependency, and everything under it |
| No third-party UI control library | §3.2 | Taking one is a reversal to be written down, not made quietly |
| Cold start p95 < 500 ms | §14, and `performance.md` measures 620–760 ms | We're already over. A second native dependency is expensive |
| Idle working set < 100 MB | §14; measured 17.6 MB | Not the binding constraint — there's room |
| Edit the source, never a rich model | §6.4 | A rich-text editor serialised back to markdown drops whatever it doesn't model. Styling may be derived; the text may not be |
| Descriptions cap at 16,383 characters | Todoist | Re-highlighting the whole document is cheap |
| Core and Presentation stay OS-agnostic | §4.5 | A Windows-only control in `App.Windows` costs nothing portable |

The character limit is the one that quietly decides most of it. At sixteen kilobytes there is no
incremental-lexing problem to solve, which is most of what an editor engine is for.

## What was considered

**One pane, rendered by default, edit on demand.** The panel shows the rendering. `Enter` or a
double-click drops into the source with the caret placed; `Esc` goes back and saves. No dependency,
no mode anybody has to manage, and whatever you are looking at gets the full width. Reading is the
common case, and reading is where the links are.

**Highlighting the source in place.** In edit mode, style the markdown as it's typed — headings
larger and bold, `**bold**` drawn bold with its markers muted, code fixed-width, links in the accent
colour. Markers stay visible; nothing hides. Markdig is already referenced and
`UsePreciseSourceLocation()` gives exact offsets to style against, the 300 ms idle timer is already
there, and `Theme` already carries text, muted and accent, so there's no palette to invent.

Three known costs, and they're the reason this isn't free:

- **Undo.** Setting `SelectionColor` or `SelectionFont` lands on the rich edit control's undo queue,
  so `Ctrl+Z` un-highlights instead of undoing what was typed. The fix is the Text Object Model:
  fetch `ITextDocument` with `EM_GETOLEINTERFACE` and wrap each restyle in `Undo(tomSuspend)` /
  `Undo(tomResume)`. Perhaps forty lines of interop. The other route — `EM_SETUNDOLIMIT` to zero and
  back — destroys the history it's meant to protect, so it isn't one.
- **Caret and scroll** have to survive a restyle: save and restore the selection, and the scroll
  position with `EM_GETSCROLLPOS` / `EM_SETSCROLLPOS`.
- **Flicker**, which is already solved here — `MarkdownView` turns drawing off around a rebuild with
  `WM_SETREDRAW` and this would do the same.

**Scintilla**, which is what the issue suggested. `Scintilla5.NET` is MIT, actively maintained
(7.0.0, July 2026), has no managed dependencies, and targets `net8.0-windows7.0`, which a
`net10.0-windows` app consumes fine. It brings a real editor: proper undo, hotspot styles for
clickable links, proportional fonts with a size per style, find and replace, margins.

What it also brings:

- A **second native dependency**, in a build that is already outside its cold-start budget. The panel
  is off by default so the control could be built on first open rather than at startup, but that
  moves the cost to a moment the user is waiting on rather than removing it. The 70 ms
  `performance.md` attributes to opening SQLite is the precedent for what a native load costs here.
- **3.73 MB** of package, into an installer whose whole appeal is that it's a couple of megabytes.
- **DPI work.** The app is PerMonitorV2. Scintilla handles that in its core, but the host has to
  forward `WM_DPICHANGED` and rescale margins and extra ascent itself, and there are open reports of
  DirectWrite rendering blurring under GDI scaling.
- **Theming by hand**, style index by style index, re-driven whenever the theme changes at runtime.
- **Accessibility and IME** that differ from a native edit control, since Scintilla draws its own
  text surface.
- **A new way for CI to fail.** The `App.Windows` tests realise controls headlessly; a Scintilla
  control needs its native library loadable in the test host.
- And the sting: Lexilla's markdown lexer is coarse. Getting headings to look like headings means
  driving styles from Markdig anyway — at which point the engine has been paid for and the
  highlighter is still ours.

**WebView2 or Monaco.** No. §14 sets a 100 MB working set explicitly on the basis of no browser
engine, and the panel is a notes box.

**AvalonEdit through `ElementHost`.** MIT and genuinely good, but it drags WPF into a WinForms
process for its startup cost and its mixed-mode focus and DPI quirks, against a cold-start figure
that is already the thing we're failing.

## What's decided

**One pane, in two steps, and no new dependency.**

First the arrangement: collapse the notes panel to a single pane showing the rendering, `Enter` or a
double-click to edit, `Esc` back. A click on a link still follows it rather than entering edit mode,
so nothing about the link behaviour changes. The panel toggle gets a key. `showPreview` and
`previewWidth` retire — the settings reader takes each key by name with a default and keeps ones it
doesn't know, so an existing `config.json` degrades to the new default rather than to nothing.

Then the highlighting, inside that same pane, driven from Markdig.

Splitting it that way is deliberate: the first step is what removes the complaint, and it costs
nothing and risks nothing. The second is what the issue asked for, and it carries all the risk. They
don't have to land together, and the first shouldn't wait for the second.

Scintilla is held in reserve rather than ruled out. The expensive parts of an editor engine —
incremental lexing, large files, folding — buy nothing at sixteen kilobytes, and everything else it
would give us costs DPI work, theming work, a native library and an installer that's four megabytes
heavier.

## What would change it

**The undo handling not working.** That's the load-bearing risk in highlighting a `RichTextBox`, and
it's a bounded thing to find out — suspend and resume around one restyle and type into it. If the
Text Object Model route doesn't hold, Scintilla becomes the right answer and §3.2 gets an honest
amendment alongside §8.3's entry on ObjectListView.

**Descriptions getting much bigger.** Whole-document restyling on every pause is fine at sixteen
kilobytes because sixteen kilobytes is small. If Todoist ever raises that ceiling by an order of
magnitude, the incremental lexer stops being something we're declining and starts being something we
need.

**Wanting an editor rather than a notes box** — find and replace, multiple carets, folding. Nothing
asks for that today, and a task description is not the place it would be asked for first.
