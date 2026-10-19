# Social preview

The picture GitHub shows when the repository is linked somewhere — 1280×640, uploaded by hand
under **Settings → General → Social preview**.

| File | |
| --- | --- |
| `card.html` | The source. The words, the mark and a cropped mock of the main window. |
| `render.ps1` | Screenshots the card with headless Edge and writes `social-preview.png`. |
| `social-preview.png` | What gets uploaded. |

## Changing it

Edit `card.html`, then:

```powershell
./assets/social-preview/render.ps1
```

and upload the new `social-preview.png`. Opening the card in a browser shows it as it'll render, so
you can check a change before running the script.

Keep anything that matters at least 80px in from every edge. GitHub crops the card differently in
different places, and the mock window is the only thing allowed to run off the side.

## What it copies

None of the card is read from the app, so it won't follow a change there on its own:

- **The mark** is `BrandIcon`'s drawing redone as SVG, using the same proportions.
- **The colours** are `ThemePalette.Dark` and `ThemePalette.ForPriority`.
- **The mock window** follows `MainForm`'s layout and `OutlineView`'s drawing — the column widths,
  the check box, the expander, the indent guides and the full-width selected row — at 1.35× so it
  reads at this size.

If one of those changes enough to show, change the card to match. The tasks, projects and labels in
the mock are made up; keep them that way.
