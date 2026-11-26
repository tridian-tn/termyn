using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// How a sidebar row is coloured, which the sidebar now does for the whole row.
/// </summary>
/// <remarks>
/// Left to the tree, the focused selection came out in Windows' colours: system blue in the light
/// theme, and in the dark one a pale band with a focus box, and the tree's own copy of the name
/// beside the one drawn here. A row's drawn onto a bitmap, since the tree paints nothing while it's
/// off every monitor, and the dark colour mode that drew the band can't be turned on in a test
/// host once a window exists.
/// </remarks>
public class SidebarSelectionTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), $"termyn-sidebar-selection-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_config))
            File.Delete(_config);

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The window's theme. Dark, which is where the tree's own selection went wrong, and where the
    /// ink for the accent isn't the white Windows writes a selection in.
    /// </summary>
    private static Theme WindowTheme => Theme.From(ThemePalette.Dark);

    /// <summary>A dark window on screen, if off every monitor, with a project in its sidebar.</summary>
    private MainForm Window()
    {
        Assert.True(new SettingsStore(_config).Save(new AppSettings { Theme = ThemePreference.Dark }));
        var window = TestWindow.Start(_config, new InMemorySnapshotStore(), new FakeApi(), out var presenter);
        TestWindow.ShowOffScreen(window);
        presenter.AddProject("Errands");
        return window;
    }

    /// <summary>Every row in a tree, at any depth.</summary>
    private static IEnumerable<TreeNode> Rows(TreeNodeCollection nodes)
        => nodes.Cast<TreeNode>().SelectMany(n => Rows(n.Nodes).Prepend(n));

    /// <summary>Draws the project's sidebar row onto a bitmap the width of the sidebar.</summary>
    private static Bitmap Drawn(MainForm window, bool lit, Action<TreeNode>? before = null)
    {
        var tree = TestWindow.Find<SidebarTree>(window);
        var row = Rows(tree.Nodes).First(n => n.Tag is SidebarNode { Label: "Errands" });
        before?.Invoke(row);

        var bitmap = new Bitmap(tree.ClientSize.Width, tree.ItemHeight);
        using var g = Graphics.FromImage(bitmap);
        window.DrawSidebarRow(g, row, new Rectangle(0, 0, bitmap.Width, bitmap.Height), lit);
        return bitmap;
    }

    /// <summary>The colour at the far end of the row, clear of anything written on it.</summary>
    private static int Background(Bitmap bitmap) => bitmap.GetPixel(bitmap.Width - 2, bitmap.Height / 2).ToArgb();

    /// <summary>Whether anything on the row is drawn in, or close to, a colour.</summary>
    private static bool Shows(Bitmap bitmap, Color colour)
    {
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var p = bitmap.GetPixel(x, y);
                if (Math.Abs(p.R - colour.R) + Math.Abs(p.G - colour.G) + Math.Abs(p.B - colour.B) < 30)
                    return true;
            }
        }

        return false;
    }

    [WinFormsFact]
    public void The_sidebar_leaves_no_part_of_a_row_to_the_tree()
    {
        // The tree's own drawing is what put Windows' selection behind the row, and its copy of
        // the name beside ours.
        using var window = Window();

        Assert.Equal(TreeViewDrawMode.OwnerDrawAll, TestWindow.Find<SidebarTree>(window).DrawMode);
    }

    [WinFormsFact]
    public void The_focused_selection_is_on_the_accent_and_written_on_it()
    {
        using var window = Window();

        using var bitmap = Drawn(window, lit: true);

        Assert.Equal(WindowTheme.Accent.ToArgb(), Background(bitmap));
        Assert.True(Shows(bitmap, WindowTheme.OnAccent), "Nothing on the row is in the ink for the accent.");
    }

    [WinFormsFact]
    public void The_selection_without_the_focus_keeps_its_quieter_mark()
    {
        using var window = Window();

        using var bitmap = Drawn(window, lit: false, row => row.BackColor = WindowTheme.Unfocused);

        Assert.Equal(WindowTheme.Unfocused.ToArgb(), Background(bitmap));
    }

    [WinFormsFact]
    public void Any_other_row_is_on_the_sidebars_background()
    {
        using var window = Window();

        using var bitmap = Drawn(window, lit: false);

        Assert.Equal(WindowTheme.Background.ToArgb(), Background(bitmap));
        Assert.False(Shows(bitmap, WindowTheme.Accent), "A row that isn't selected has the accent on it.");
    }
}
