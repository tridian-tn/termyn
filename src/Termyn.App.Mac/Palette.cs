using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The few colours that are Todoist's rather than the system's.
/// </summary>
/// <remarks>
/// Everything else on screen is a semantic system colour, so dark mode, the accent colour and
/// Increase Contrast follow the system with nothing done here. Todoist's colours appear only where
/// they say something — a project's dot, a label, a priority — and read the same as in Todoist.
/// </remarks>
internal static class Palette
{
    /// <summary>A Todoist colour, as AppKit draws it.</summary>
    public static NSColor Of(Rgb colour) => NSColor.FromSrgb(colour.R / 255f, colour.G / 255f, colour.B / 255f, 1f);

    /// <summary>
    /// The ring of a task's circle: Todoist's colour for P1 to P3, and the system's quietest for the
    /// rest, which have no priority to speak of.
    /// </summary>
    public static NSColor Priority(Priority priority)
        => priority == Core.Model.Priority.P4 ? NSColor.TertiaryLabel : Of(ThemePalette.ForPriority(priority));

    /// <summary>
    /// A project's dot as a run of text, with the space that follows it.
    /// </summary>
    /// <remarks>
    /// A ringed dot is stroked in the selection's text colour. The negative width fills the glyph
    /// as well as stroking it, which a positive width wouldn't: the dot would come out hollow.
    /// </remarks>
    /// <param name="dot">The dot's colour and whether to ring it</param>
    /// <param name="font">The font the rest of the line is in</param>
    /// <returns>The dot and a space, ready to go in front of the project's name</returns>
    public static NSAttributedString Dot(ColourDot dot, NSFont? font)
    {
        var attributes = new NSStringAttributes { Font = font, ForegroundColor = Of(dot.Colour) };
        if (dot.Ringed)
        {
            attributes.StrokeColor = NSColor.AlternateSelectedControlText;
            attributes.StrokeWidth = -8f;
        }

        var run = new NSMutableAttributedString("●", attributes);
        run.Append(new NSAttributedString(" ", new NSStringAttributes { Font = font }));
        return run;
    }
}
