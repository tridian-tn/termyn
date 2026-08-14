# The comments pane

Comments arrived after the description editor and went into the same panel. This records what was
decided and why, so the parts that look arbitrary aren't re-argued from scratch.

## A third mode, not a fourth pane

The panel under the outline already switches between the markdown and its rendering — one pane, two
things it can be. Comments are a third. They are not a second panel under the description, and not a
splitter across it.

That follows the conclusion `description-editor.md` had already reached the hard way: the panel
opens at 260 px, which is about nine lines, and splitting it again leaves two halves that are each
too short to be worth reading. The comments take the whole panel while they are showing, and leaving
them puts back whichever of the other two was there.

`Ctrl+M` toggles it. Leaving the description mid-edit saves it first, as closing the panel does —
switching away is the box losing the user as surely as the focus leaving it.

## A list, not one rendered document

The description is drawn as a single rendered document, and comments could have been too — one
markdown view with a rule between each. That reads well and gives links and hover targets for free.

It was rejected because a comment is a thing you act on. Editing one and deleting one both need the
pane to know *which*, and a rendered document has no notion of the third paragraph being a separate
object. So the pane is an owner-drawn list, measured per row: a comment of three lines takes three
lines, where a fixed row height would either clip most of them or spend a short panel on the brief
ones.

The cost is that comments aren't rendered as markdown — they're drawn as the text they're stored as.
That's the same text the account holds, and consistent with editing the description as its source
rather than as a rich model, but it is a real loss and the obvious thing to come back to.

## Editing happens in the box that posts

Selecting a comment and pressing `F2` or `Enter` loads it into the compose box, which then saves
instead of posting; `Esc` abandons it. The alternative — a row that turns into an editor — has
whatever height that row had, in a panel that is short to begin with, and gives two places to type a
comment where one will do.

## Project comments come off the sidebar

Todoist files task comments under `notes` and project comments under `project_notes`, and writes
both with the same `note_*` command. Both are supported.

The pane follows the task the outline is on. A project has nothing in the outline to select, so its
comments are reached by **Comments on project** in the Organise menu, over a selected project. The
pane then stays on that project until the outline selection actually moves — which is why it tracks
the last row it followed rather than reading the selection each time it redraws. A re-render is not
a move, and a sync causes one every forty-five seconds.

## The command can't say which resource it means

Because both comment types share `note_add` / `note_update` / `note_delete`, the command name alone
can't say where a write lands. An add says so in its args — `item_id` or `project_id` — but an
update and a delete carry only the comment's id, so the model is asked where that id is currently
filed.

This is worth stating plainly because getting it wrong is invisible until it isn't: the pending-write
set is keyed by resource type, so a project comment recorded under `notes` fails to shield itself,
the server's copy lands on top of the local edit, and the sync token has already advanced past that
change. The edit is then gone with nothing reporting it. `CommentTests` covers exactly this.

## Attachments are named, not fetched

A comment can carry a file. Fetching one is a later phase, but the file's *name* is read now,
because a comment can have an attachment and no text at all — and one drawn from its text alone is a
blank row that reads as a comment that failed to load rather than as a file.

## What isn't here

- Comments aren't rendered as markdown (above).
- The outline marks a task that has comments with `💬` and nothing more — no count, and no way to
  filter or search by them.
- Author identity isn't modelled. Everything is shown as though it's yours, which it is until shared
  projects are supported.
