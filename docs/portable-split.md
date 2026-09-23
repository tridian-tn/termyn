# What belongs below the UI

`Core` and `Presentation` target `net10.0` rather than `net10.0-windows` so that a future native
Linux or macOS client can reuse everything below the UI. The framework targets enforce half of
that: the compiler stops a Windows API reaching the portable projects. Nothing enforces the other
half. Portable logic can be written in `Termyn.App.Windows` and stay there, and the build is
perfectly happy about it.

This is the read-through that checked which way it went, and what it found.

## What was looked at

All 28 files of `src/Termyn.App.Windows`, 10,156 lines. Of each piece of logic: does it need a
`Control`, a `Graphics` or a window handle to be what it is? What doesn't — grouping, ordering,
formatting, filtering, deciding what a key press means — has an answer that's the same whatever
draws it, and belongs in `Presentation`. Anything that is domain rather than presentation belongs
in `Core`.

## What held

Most of it, and this is the more useful half of the answer. Every place that could plausibly have
drifted already has a portable half doing the deciding:

| The Windows side | Already asks |
|---|---|
| `Theme` | `ThemePalette` in `Core/Settings`, which keeps colours as `Rgb` so Core stays free of any toolkit's colour type |
| `MainForm.FillMenu` | `Presentation.Commands.StateOf`, for every label, enabled and checked state |
| `MoveTaskForm` | `MoveDestinations.Rank` |
| `SettingsForm` | `Core.Settings.HotkeyBinding` |
| `QuickAddForm` | `CapturePreviewText` |
| `MarkdownEditor` | `Presentation.MarkdownHighlight` |
| `AppVersion` | `Core.Links`, which vets every URL before it reaches ShellExecute |

And a good deal of the project has nothing to move because there is nothing in it but drawing:
every `Draw*` method, the layout of every dialog, `BufferedTreeView`, `Dots`, `Faces`,
`CalendarGlyph`, `HintTextBox`, `SearchBox`, `DetailHeader`, `StartupTrace`, and `Program.cs`,
which is a composition root and should be.

## What didn't

Three groups, in order of what they cost to move.

### Clean moves

Each of these is a pure function over a type that already lives in `Presentation` or `Core`. A
second client would re-derive every one of them, identically.

| What | Where it is | Where it goes |
|---|---|---|
| `CellOf`, with `ContentOf`, `DueOf`, `LabelsOf` and `PaintOf`, and the `TaskColumn`/`CellPaint` enums | `OutlineView` | `Presentation` |
| `PastHeading` — which row a selection lands on, having hit a day heading | `OutlineView` | `Presentation` |
| `MetaOf` and `BodyOf` — what a comment row reads | `CommentsView` | beside `CommentRow` |
| The three sentences `ShowReason` composes from a `FailedChange` | `FailedChangesForm` | `Failures` |
| `ReminderRow.ToString`, with `Moment`, `Describe` and `Plural`; and `CanRemove` | `ReminderForm` | `Presentation` |
| `SitsInDotColumn` | `MainForm` | `Sidebar` |
| The selection rule in `ShowMatches` — browsing rests on where the task already is, searching takes the best match that isn't | `MoveTaskForm` | `MoveDestinations` |

About 175 lines between them. `PastHeading` is the clearest of the shape: it is index arithmetic
over a list of rows and the direction the selection was travelling, and it sits directly above
`Step`, which is `SelectedIndices` and `FocusedItem` and could not be anywhere else. The line
between the two is exactly the line this document is about.

### Moves that need a portable type first

**`ProjectDot` and `LabelRuns`** in `OutlineView`. The decisions are portable — nothing on a
selected row, because the accent is behind it; a label whose colour hasn't synced yet reads the
way every label used to. Both return `System.Drawing.Color`, which is what keeps them here, and
Core already has `Rgb` for precisely this reason. Re-typed, they move.

**`Theme.Blend`, `OnAccent` and `Unfocused`.** Derived colours — a selection faded most of the way
into the background, the text drawn on an accent — decided in the Windows project while the palette
they derive from is in `Core/Settings`. The reasoning behind `Unfocused`'s 0.78 is written down in
the Windows project and belongs with the colours it is about.

**The shortcut table** — `MainForm.Shortcuts`, `CommandFor`, `ShortcutFor` and `ShortcutText`, about
110 lines. Which keystroke means which command, and how a menu writes it out, is the same answer on
any desktop; `ShortcutTests` already runs without a window. The cost is that all of it is keyed on
the WinForms `Keys` enum, so moving it needs a portable key type and a `Keys`-to-portable map left
behind here. That is a design decision, not a move, which is why it is in this group and not the
one above.

### The Markdig walk

`MarkdownView`'s `WriteBlock`, `WriteList`, `WriteCode`, `WriteParagraph`, `WriteInline` and
`Write` — about 250 lines deciding what every run of a description says, how far it indents and
which style it takes. It is the largest piece of portable reasoning in the project and the most
expensive to extract, because there is no intermediate run list: each run is written into the
rich text document as the walk reaches it.

Extracting it means producing `(text, style, indent, source span)` runs in `Presentation` and
leaving the RTF emission here. That is a restructure rather than a move. It would sit beside
`MarkdownHighlight`, which already does the source-side equivalent and says in its own remarks that
it is there "because it is the same answer whatever draws it" — the two would then be the read side
and the write side of one idea, in one place.

## What it costs to leave them

Not much, today. There is no second client, so nothing is being duplicated yet, and none of this is
a bug.

The cost that is real now is in the tests. Every test in `Termyn.App.Windows.Tests` must carry
`[WinFormsFact]` — single-threaded apartment, message pump — and `ApartmentTests` fails the build
if one doesn't. They also run one at a time, since rich edit controls on several threads at once is
what [#115](https://github.com/tridian-tn/termyn/issues/115) turned out to be. That is 5,739 lines
of test running on a path that xUnit does not give a test by default.

Several of the clean moves are already tested through a control that only exists to be constructed.
`OutlineColumnTests` stands an `OutlineView` up to call a static method; so do `OutlineColourTests`
and `HeadingRowTests`. Moving the logic takes roughly 500 lines of test off the apartment path with
it. That is a better argument for doing this than tidiness is.

## What isn't a move

**Nothing enforces the boundary.** No test, no analyzer, no build rule. The split is held by the
framework targets, which guard one direction, and by everyone remembering, which guards the other.
A guard is worth having regardless of how much of the above gets moved.

**`SettingsForm.Sync` restates half of `HotkeyBinding.IsValid`.** `needsModifier` recomputes the
Ctrl-Alt-Win half of a rule Core already owns and can already answer. A duplication rather than a
misplacement, and a small one, but it is the shape of thing this read-through was looking for.

## The order

The clean moves should go before
[#146](https://github.com/tridian-tn/termyn/issues/146). That issue moves the WinForms text into
`App.Windows/Strings.resx`, and names `ReminderForm`, `CommentsView` and `FailedChangesForm` among
its targets — three of the seven. Catalogues are per-project, so translating first and moving
afterwards writes those strings into one catalogue and then moves them into another. Moving first
costs nothing.

## What was raised

| Issue | What it covers |
|---|---|
| [#185](https://github.com/tridian-tn/termyn/issues/185) | The seven clean moves, as one branch |
| [#186](https://github.com/tridian-tn/termyn/issues/186) | `ProjectDot` and `LabelRuns` re-typed to `Rgb`; `Blend`, `OnAccent` and `Unfocused` onto `ThemePalette` |
| [#187](https://github.com/tridian-tn/termyn/issues/187) | The shortcut table, and the portable key type it needs first |
| [#188](https://github.com/tridian-tn/termyn/issues/188) | The Markdig walk |
| [#189](https://github.com/tridian-tn/termyn/issues/189) | A guard, so this can't drift again unnoticed |
| [#190](https://github.com/tridian-tn/termyn/issues/190) | The `HotkeyBinding` duplication |

## What would change it

**A second client actually starting.** Everything above is currently a judgement about duplication
that hasn't happened. The moment a Linux client exists, the tier that needs a portable type first
stops being a preference and becomes the difference between one key map and two.

**Not #115 any more.** It could have forced the markdown tests off the control, and the walk out
with them as a side effect. It was fixed another way — the tests run one at a time, so no two
threads use a rich edit control at once — so the walk stays where it is and the case for moving it
is only the second-client one.

**Descriptions, or the outline, growing a second renderer.** The rendered view and the editor
already agree about the source because `MarkdownHighlight` is shared. Anything that has to agree
with them about the *rendering* would make the walk's current home untenable.
