# The description editor

A task's description is the one place in Termyn where somebody writes more than a line. This records
what the panel was, why the two-pane arrangement came out, what was tried in its place, and what
building both candidates showed.

Both were built rather than reasoned about, and the building changed the answer twice. One claim in
an earlier revision — that Lexilla's lexer cannot draw a heading — was simply wrong, and is corrected
below; it was a property left unset rather than a limit of the lexer.

## What it was

The description panel sat under the outline and was off until you asked for it. Turned on, it split again:
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

## Why it changed

Four things compounded, and they compounded in the same direction.

**It was the same text twice.** The left half was set in a fixed-width face for no reason except
that without it the two halves of a description with no formatting in it are identical, and the
panel looks like it has done nothing. That is a workaround for the arrangement, not a feature of it.

**Both halves were too small.** The panel opens 180 px tall — about nine lines — and the vertical
split took 320 px off a pane that was already short.

**The halves had different powers.** Only the left one took typing. Only the right one had clickable
links and hover targets. So you worked in one and looked at the other, and the panel was at its
worst exactly when the description had something in it worth following.

**It cost two toggles and no keys.** Show description and show formatted description were both
menu-only, in an app whose whole argument is the keyboard.

None of this is in the spec. §8 describes the sidebar and the outline and says nothing about a
description editor.

## What has to hold

| Constraint | Where from | What it rules on |
|---|---|---|
| MIT-clean | §8.3 — ObjectListView was rejected over GPLv3 | Any dependency, and everything under it |
| No third-party UI control library | §3.2 | Taking one is a reversal to be written down, not made quietly |
| Cold start p95 < 500 ms | §14, and `performance.md` measures 620–760 ms | We're already over. A second native dependency is expensive |
| Edit the source, never a rich model | §6.4 | A rich-text editor serialised back to markdown drops whatever it doesn't model. Styling may be derived; the text may not be |
| Descriptions cap at 16,383 characters | Todoist | Re-highlighting the whole document is cheap |
| Core and Presentation stay OS-agnostic | §4.5 | A Windows-only control in `App.Windows` costs nothing portable |

## The thing that decided it

Both candidates need to know which part of some markdown is a heading, which is a marker, which is
an address. Writing that **once**, in `Presentation` with no window anywhere near it, turned out to
be the whole decision — because once the highlighting is shared, the only thing left to compare is
the control, and the control turns out not to buy much.

`MarkdownHighlight` reads markdown and returns runs that tile the source exactly: no gaps, no
overlaps, in order. It reads as a style per character and joins them up, rather than as spans laid
over each other — markdown nests, and overlapping spans leave the caller to decide which wins.
Twenty-nine tests, none of which need a control to look at.

## What each candidate then needed

### A rich text box drawing its own source

The box shows the markdown, styled from the shared list. Two things had to be built, and both were
found by building them.

**The control's undo queue can't be used, and the documented way round it does not work.** A rich
edit control records applying a colour or a font as an undoable action, so a box that highlights
what you type answers Ctrl+Z by un-highlighting. The documented fix is to suspend the queue through
the Text Object Model. That was tried, with `ITextDocument` declared far enough down its vtable to
prove the calls land — `Freeze` answers 1, `Unfreeze` answers 0 — and **the formatting still goes on
the queue**. Undo #1 removed the bold, undo #2 removed the colour, and the typing was never reached.

So the queue is switched off at the source with `EM_SETUNDOLIMIT`, and `DescriptionHistory` stands in for
it: one state per pause in the typing, which is the granularity an editor undoes at anyway. 153
lines, 13 tests, no window.

**Styling run by run is unusably slow, and the shape of the fix is not the obvious one.** Selecting
each run and setting its font and colour measured **1,583 ms** for a full-length description — 2,981
runs, each a round trip that reflows the control. Caching the fonts only took it to 991 ms, because
the fonts were never the cost. Built instead as one rich text document and handed over in a single
message it is **18 ms**, and flat: 3 ms at five hundred characters.

| Description length | Run at a time | As one document |
|---|---|---|
| 500 chars | 26 ms | 3 ms |
| 2,000 chars | 114 ms | 12 ms |
| 8,000 chars | 541 ms | 10 ms |
| 16,383 chars | 1,583 ms | 18 ms |

Handing over a whole document means a description becomes document syntax, so the escaping has tests
of its own — braces, backslashes, tabs, an em dash, Japanese, an emoji, and a description that opens with an
RTF header. An unescaped brace ends the document early and takes the rest of the description with it.

### Scintilla

`Scintilla5.NET` is MIT, actively maintained (7.0.0, July 2026), has no managed dependencies, and
targets `net8.0-windows7.0`, which a `net10.0-windows` app consumes fine. The native library loads in
the headless test host without ceremony, so the CI worry came to nothing.

**Its own markdown lexer wants configuring, and then has three defects on ordinary descriptions.**

By default Lexilla styles a heading's hash and leaves the words as body text. That is a setting
rather than a limit: `lexer.markdown.header.eolfill` fills the style to the end of the line, and with
it on a heading is a heading. The property isn't returned by `PropertyNames()`, so it doesn't turn up
by asking the lexer what it takes — it turns up by reading `LexMarkdown.cxx`. An earlier revision of
this note had it as a limit and was wrong.

What survives configuring:

- `[ ]` is lexed as the opening of a link, so every box in a checklist — the commonest shape a
  description takes — is drawn in the link colour with a hand pointer, and then does nothing when
  clicked. That is exactly the "looks clickable and isn't" the rendered view went out of its way to
  avoid.
- Bare URLs are missed in both the plain and the angle-bracket form. The rendered view had tests for
  both; the angle-bracket one was a bug it had already fixed. People paste these far more often than
  they write a link.
- A list or a quotation on the very first line of a description is missed entirely — the same text
  one paragraph down is found. Descriptions that open with a list are not an edge case. Headings
  don't share this.

Each of those is a common shape drawn wrongly rather than an exotic one drawn imperfectly, and none
is a setting. Fixing any of them means taking the lexer off — `LexerName` of null makes the control a
container — and styling from the shared list through `StyleNeeded`. Which is the point: **the
highlighter ends up being ours either way**, though by a narrower margin than this note first
claimed.

The incremental lexing that was half the argument mostly doesn't pay either. Scintilla asks only for
the stretch it needs drawn, but our highlighter parses the whole description to answer, so only the
styling calls are saved: **5.2 ms against 18 ms**. Both are inside a 300 ms pause.

### Not taken

**WebView2 or Monaco.** §14 sets a 100 MB working set explicitly on the basis of no browser engine.

**AvalonEdit through `ElementHost`.** MIT and good, but it drags WPF into a WinForms process for its
startup cost and its mixed-mode focus and DPI quirks, against a cold start already failing.

## What it costs, measured

| | Rich text box | Scintilla |
|---|---|---|
| Extra production code | 459 lines | 238 lines |
| Undo | hand-rolled, 13 tests | free, untouched by styling |
| Published output | **5.3 MB** | **14.9 MB** |
| First control creation | nil | 66–90 ms warm, 282 ms cold |
| Styling a full description | 18 ms | 5.2 ms |
| DPI | inherited from WinForms | needs `WM_DPICHANGED` forwarding and margin rescaling — not built |
| Theming | one assignment | every style index by hand |
| Rendered reading view | kept | gone |

The size is worse than the library: the package ships x86, x64 **and** arm64 native pairs under
`runtimes/` plus a flattened copy beside the executable, so about 4.6 MB of the increase is native
code for architectures this build doesn't target. Prunable, but somebody has to prune it.

## What's decided

**One pane, and the rich text box.**

The panel is a single pane showing the rendering. `Enter`, `F2` or a double-click opens the markdown
to type into, with the caret where the reading was pointing; `Escape` or the focus leaving puts it
back and saves. A single click still selects and still follows a link. A task with no description opens
ready to write. `Ctrl+E` opens and closes the panel; `showPreview` and `previewWidth` retire.

The case turns on how much of the highlighting the control can be left to do. Configured, Lexilla
does most of it — but not checklists, not bare URLs, and not a list or quotation opening a
description, which between them cover a lot of what people actually write. Fixing those means
supplying the styling anyway, and once it is supplied the control is being bought for one thing:
undo that styling can't corrupt, worth 153 lines and 13 tests.

Against that it costs 9.7 MB, a native load on first open, DPI work still unbuilt, theming by hand,
its own text surface for screen readers and IME, and the rendered reading view. 153 lines of
portable, tested logic is the cheaper side of that, and §3.2 stands unamended.

It is a narrower call than it looked before the lexer was configured properly. Somebody willing to
live with checkboxes drawn as links and bare URLs drawn as prose could have the highlighting for
almost no code — that is a real option, and it is being declined on what it draws wrongly rather
than on what it cannot draw.

## What would change it

The three conditions this note first named have now been tested, and all three resolved differently
from the guess — which is the argument for having built both rather than choosing on paper.

**Descriptions getting much bigger.** Still live. Whole-document restyling is fine at sixteen
kilobytes because sixteen kilobytes is small. An order of magnitude more and the incremental lexer
stops being something we're declining and starts being something we need.

**Wanting an editor rather than a description box** — find and replace, multiple carets, folding. Nothing
asks for that today.

**The undo behaviour changing.** If a future rich edit honoured `Undo(tomSuspend)`, `DescriptionHistory`
could go. Not something to wait for, and the 153 lines are portable in a way the control isn't.

## What running it turned up

Both builds were run against a real account, which found two faults no test would have:

- **[#39](https://github.com/tridian-tn/termyn/issues/39)** — the outline keeps its selection by
  index, so a sync that reorders rows retargets the description panel mid-edit and the description saves
  to a task nobody selected. It cost a real description to find. `DescriptionDraft` defends the text
  against a sync and the target moves out from under it.
- **[#40](https://github.com/tridian-tn/termyn/issues/40)** — a malformed cache throws out of
  `SqliteSnapshotStore`'s constructor and the window never appears. A cache should be rebuilt, not
  fatal.

And one thing about the account rather than the app: **Todoist rewrites a bare URL into a titled
markdown link server-side**. A description can come back from a sync differently from how it was
sent, which is worth knowing wherever a draft is compared against what was saved.
