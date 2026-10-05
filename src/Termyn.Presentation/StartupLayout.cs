namespace Termyn.Presentation;

/// <summary>
/// How big the window and its panes start out, before the user has moved anything.
/// </summary>
/// <remarks>
/// Figures for a display at 100%, scaled to whatever the screen runs at. They used to be pixels,
/// which at 125% made a smaller window with bigger text in it, and the outline's columns added up
/// to more than the outline the default window gave them — Labels started past the right-hand edge.
///
/// Here rather than in the window so the arithmetic can be checked at any scale without a screen
/// that runs at it.
/// </remarks>
public static class StartupLayout
{
    /// <summary>The window's width at 100%.</summary>
    public const int WindowWidth = 1100;

    /// <summary>The window's height at 100%.</summary>
    public const int WindowHeight = 700;

    /// <summary>The narrowest the window can be made, at 100%.</summary>
    public const int MinimumWidth = 640;

    /// <summary>The shortest the window can be made, at 100%.</summary>
    public const int MinimumHeight = 400;

    /// <summary>The sidebar's width at 100%.</summary>
    public const int SidebarWidth = 220;

    /// <summary>The narrowest a saved sidebar width is held to, at 100%.</summary>
    public const int SidebarLeast = 120;

    /// <summary>What a saved sidebar width has to leave the outline at least, at 100%.</summary>
    public const int OutlineLeast = 200;

    /// <summary>
    /// How tall the description panel is at 100%.
    /// </summary>
    /// <remarks>
    /// Taller than it was when the panel was split down the middle. One pane gets the whole width,
    /// so the height is the only thing left deciding how much of a description you can see at once.
    /// </remarks>
    public const int DescriptionHeight = 260;

    /// <summary>The narrowest the task column is made to fit the others in, at 100%.</summary>
    public const int TaskColumnLeast = 150;

    /// <summary>
    /// The most of the screen's working area a new window takes, so a small screen still shows its
    /// edges and a little of whatever is behind it.
    /// </summary>
    private const double MostOfScreen = 0.9;

    /// <summary>
    /// A column's width at 100%.
    /// </summary>
    /// <remarks>
    /// The task column's is where it starts. When no widths have been saved, it then takes whatever
    /// the others leave of the outline — see <see cref="TaskColumnWidth"/>.
    /// </remarks>
    /// <param name="column">The column</param>
    /// <returns>Its width, or nought for a column the outline hasn't got</returns>
    public static int ColumnWidth(TaskColumn column) => column switch
    {
        TaskColumn.Content => 360,
        TaskColumn.Priority => 40,
        TaskColumn.Project => 120,
        TaskColumn.Due => 110,
        TaskColumn.Deadline => 100,
        TaskColumn.Labels => 120,
        _ => 0,
    };

    /// <summary>A size at 100% as it is at a display's scale.</summary>
    /// <param name="logical">The size at 100%</param>
    /// <param name="dpi">The display's dots per inch, which is 96 at 100%</param>
    /// <returns>The size in the display's pixels</returns>
    public static int Scaled(int logical, int dpi) => (int)Math.Round(logical * dpi / 96.0);

    /// <summary>
    /// The size a new window opens at.
    /// </summary>
    /// <param name="dpi">The dots per inch of the screen it opens on</param>
    /// <param name="workingWidth">How wide that screen's working area is, in its pixels</param>
    /// <param name="workingHeight">How tall that screen's working area is, in its pixels</param>
    /// <returns>The default at the screen's scale, held to most of what the screen has room for</returns>
    public static (int Width, int Height) Window(int dpi, int workingWidth, int workingHeight)
        => (Math.Min(Scaled(WindowWidth, dpi), (int)(workingWidth * MostOfScreen)),
            Math.Min(Scaled(WindowHeight, dpi), (int)(workingHeight * MostOfScreen)));

    /// <summary>
    /// The smallest the window can be made.
    /// </summary>
    /// <remarks>
    /// Held to the same share of the screen as a new window, which it would otherwise push past: a
    /// small screen at a high scale has room for less than the minimum at that scale.
    /// </remarks>
    /// <param name="dpi">The dots per inch of the screen it's on</param>
    /// <param name="workingWidth">How wide that screen's working area is, in its pixels</param>
    /// <param name="workingHeight">How tall that screen's working area is, in its pixels</param>
    /// <returns>The least outer width and height, in pixels</returns>
    public static (int Width, int Height) Minimum(int dpi, int workingWidth, int workingHeight)
        => (Math.Min(Scaled(MinimumWidth, dpi), (int)(workingWidth * MostOfScreen)),
            Math.Min(Scaled(MinimumHeight, dpi), (int)(workingHeight * MostOfScreen)));

    /// <summary>
    /// How wide the task column is made when no widths have been saved.
    /// </summary>
    /// <remarks>
    /// Whatever the other columns leave of the outline, so every one of them shows without scrolling
    /// sideways, however big the window opened. Never narrower than <see cref="TaskColumnLeast"/>,
    /// past which a screen is too small for all of them and the task's own words matter most.
    /// </remarks>
    /// <param name="room">How wide the outline is, in pixels, less its scroll bar</param>
    /// <param name="others">How wide the other columns are together, in pixels</param>
    /// <param name="dpi">The outline's dots per inch</param>
    /// <returns>The task column's width in pixels</returns>
    public static int TaskColumnWidth(int room, int others, int dpi)
        => Math.Max(room - others, Scaled(TaskColumnLeast, dpi));
}
