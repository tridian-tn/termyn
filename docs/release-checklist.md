# Release checklist

Run by hand against the release build before the draft release is published. It covers what the
automated tests can't reach: the installer on a machine without the runtime, the desktop's own
pieces (the hotkey, the tray, launch at login), and a real account going offline and back.

A step that fails is an issue before the release, not a note after it.

## Before you start

- [ ] The build is the one attached to the draft release for the tag. The version in both file
      names matches the tag.
- [ ] A Windows 10 1809+ or 11 x64 VM **without** the .NET 10 Desktop Runtime, snapshotted so the
      install can be run again. A second snapshot with the previous release installed and signed in
      is needed for the upgrade step.
- [ ] A throwaway Todoist account, never your own: steps below delete things, and removing an
      attachment deletes the file from Todoist too. In it:
  - [ ] a project called `Home`
  - [ ] a task repeating `every weekday`
  - [ ] a task with a duration set in the web app (Termyn doesn't edit durations, which is the point)
  - [ ] a filter Termyn can't read, such as `assigned to: Sam`
- [ ] A second throwaway account, for switching accounts.

## Install

- [ ] Run the setup on the VM without the runtime. SmartScreen's warning is expected while the
      builds are unsigned.
- [ ] It offers to open the .NET 10 download page. Declining installs anyway and says so. Then
      install the runtime.
- [ ] No elevation prompt at any point. It installs to `%LOCALAPPDATA%\Programs\Termyn`.
- [ ] A Start menu shortcut; a desktop one only if ticked.
- [ ] On the previous-release snapshot, install over the top while Termyn is running: it closes
      Termyn first, and it opens still signed in with its settings.

## First run

- [ ] It asks to **Connect Termyn to Todoist**. A wrong token says *That token was rejected*.
- [ ] The right token syncs, and the status line reaches *Synced*.
- [ ] **Help › About Termyn** shows the release's version.
- [ ] The window opens centred, at a size that suits the screen's scaling, with every column of the
      outline in view. Drag a column's edge and restart: it's the width it was left at.

## Hotkey, tray, launch at login

- [ ] **Ctrl+Alt+A** from another application opens quick add, and what's added appears.
- [ ] **Settings…** to another combination: the new one works and the old one doesn't. Put it back.
- [ ] Closing the window leaves Termyn in the tray. Its menu has **Open Termyn**, **Quick add…**,
      recent views, **Sync now**, **Settings…**, **Check for updates…** and **Exit**.
- [ ] **Check for updates…** answers without an error.
- [ ] Open a label, end Termyn from Task Manager, and start it again: it opens on that label. Exit,
      delete the label in the web app, and start it again: it moves to Today once the first sync
      lands.
- [ ] Tick **Start Termyn when I sign in**, sign out of Windows and back in: Termyn starts in the
      tray without opening a window.

## Offline and back

Disconnect the network.

- [ ] The status line says *Offline (showing cached)*.
- [ ] Quick-add `Buy milk tomorrow p1 #Home`: it appears straight away due tomorrow, P1, in
      `Home`, and the status line counts *1 pending*.
- [ ] Tick a task off, edit a description and add a comment. Each shows straight away, and the
      pending count goes up.
- [ ] A comment with a file not yet downloaded shows the file's name and size, and opening it says
      so rather than doing nothing: *… hasn't been downloaded, and Todoist can't be reached. It'll
      be there to try again when you're back online.*
- [ ] Add a task to a project, then delete that project in the web app.
- [ ] Add a project, open it, and quick-add a task while it's open. Leave it open.

Reconnect.

- [ ] The pending count goes to nothing, and the web app has the new task, the tick, the
      description and the comment's text as they were written.
- [ ] The new project is still open with its task in it, and still lit in the sidebar. Open Today:
      the tray's menu offers the new project among its recent views.
- [ ] The task added to the deleted project is taken back, with what was queued under it, and the
      status line says *1 failed*. Clicking it says why.

## Attachments

- [ ] **Attach a file…** on a comment uploads it, and the web app shows it.
- [ ] Opening it downloads with progress and opens it in its own application. Opening it again
      doesn't download it again.
- [ ] **Remove attachment** says the file goes from Todoist as well. After confirming, it's gone
      here and in the web app.
- [ ] A file larger than the plan allows is refused before anything uploads, naming the limit:
      *… and this Todoist plan takes files up to … MB.*
- [ ] Add a large file (100 MB or so) in the web app. After a sync, nothing new is in
      `%LOCALAPPDATA%\Termyn\attachments` until it's opened.
- [ ] Set **Downloads: keep up to (MB)** to 1 and open two files bigger than that. The older one
      goes from the attachments folder, and opening it again fetches it rather than failing.

## The rest of the acceptance criteria

The remaining §16.2 criteria in the spec, in the words of the app.

- [ ] Change the priority of the task with a duration. After it syncs, the web app still shows the
      duration.
- [ ] **Space** on the `every weekday` task: its row is ticked and struck through, and its due
      column reads *↻ advancing…*. It comes back unstruck with the next weekday once the server
      replies. Offline, it stays that way until the reconnect.
- [ ] Delete a task in the web app. After **Sync now** it's gone from Termyn.
- [ ] Open the filter Termyn can't read: the list is empty, with *Termyn can't read this filter*
      above it, and **Open in Todoist** opens that filter in the browser.
- [ ] Offline, add a task, then **File › Sign out…**: it says a change hasn't reached Todoist and
      will be lost. After confirming, the token dialog shows without a restart, and
      `%LOCALAPPDATA%\Termyn` holds no cache, outbox or attachments.
- [ ] Sign in to the second account: nothing of the first shows up. No task, no comment, no file,
      nothing in **What you've done…**, and no folds.

## Before uninstalling

- [ ] `%LOCALAPPDATA%\Termyn\logs` has nothing unexpected in it.

## Uninstall

- [ ] It asks whether to remove your data, with **No** as the default. After **No**,
      `%APPDATA%\Termyn` and `%LOCALAPPDATA%\Termyn` are still there, and reinstalling opens signed in.
- [ ] Uninstall again and answer **Yes**: both folders go, and so do the shortcuts and the
      launch-at-login entry.

Then publish the draft.
