namespace Termyn.App.Windows;

/// <summary>The icon every window shows in its title bar, the taskbar and Alt+Tab.</summary>
/// <remarks>
/// The executable's icon only reaches Explorer and shortcuts; a form doesn't inherit it, and
/// without one of its own shows the stock WinForms icon. This is the same <c>assets\termyn.ico</c>,
/// embedded whole so each window can pick the frame that suits its size and DPI.
/// </remarks>
internal static class AppIcon
{
    /// <summary>One shared instance: a form doesn't dispose the icon it's given.</summary>
    public static Icon Window { get; } = Load();

    private static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Termyn.ico")
            ?? throw new InvalidOperationException("Termyn.ico isn't embedded in the app.");
        return new Icon(stream);
    }
}
