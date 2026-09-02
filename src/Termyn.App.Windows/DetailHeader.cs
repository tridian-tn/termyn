using System.ComponentModel;
using Termyn.Core.Settings;

namespace Termyn.App.Windows;

/// <summary>
/// The line across the top of the detail panel, naming what the panel is about.
/// </summary>
/// <remarks>
/// The tabs below say which side of a task you are looking at and never which task, and the outline
/// can be scrolled away from the row that is selected — so without this the panel is a description
/// and a conversation belonging to nothing you can see. Some clutter for an answer that was
/// otherwise only available by scrolling back.
///
/// A panel rather than a bare label so there is somewhere for the rest to go. The name fills what is
/// left after anything docked to the right of it, which is where read-only detail about the subject
/// — a due date, a count, whatever earns its room — can be hung without moving the name.
/// </remarks>
internal sealed class DetailHeader : Panel
{
    private readonly Label _name;
    private Theme _theme = Theme.Resolve(ThemePreference.System);

    public DetailHeader()
    {
        Dock = DockStyle.Top;
        Height = 24;

        // The bottom row kept back from whatever is docked inside, so the rule drawn there isn't
        // painted over by a child filling the panel.
        Padding = new Padding(0, 0, 0, 1);

        _name = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,

            // A task is called whatever its own text says, and some of them are sentences.
            AutoEllipsis = true,
            Padding = new Padding(6, 0, 6, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        Controls.Add(_name);
        ApplyTheme();
    }

    /// <summary>What the panel is about, or empty when the selection names nothing it can show.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Subject
    {
        get => _name.Text;
        set => _name.Text = value;
    }

    /// <summary>The colours to draw with.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Theme Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            ApplyTheme();
        }
    }

    /// <summary>
    /// Draws the rule along the bottom.
    /// </summary>
    /// <remarks>
    /// Without it the name sits straight on the tab strip and reads as part of it. Drawn on the row
    /// the padding holds back, which is the only part of the panel a docked child doesn't cover.
    /// </remarks>
    /// <param name="e">Where to draw</param>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        using var pen = new Pen(_theme.Border);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

    /// <summary>Puts the current colours on, which is everything the header has of its own.</summary>
    private void ApplyTheme()
    {
        BackColor = _theme.Panel;
        _name.BackColor = _theme.Panel;
        _name.ForeColor = _theme.Muted;
        Invalidate();
    }
}
