using Termyn.Core.Model;
using Termyn.Core.Settings;

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
}
