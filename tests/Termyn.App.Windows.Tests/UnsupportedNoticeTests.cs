using System.Drawing;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The strip that says a filter can't be read here and offers the way out to Todoist.
/// </summary>
/// <remarks>
/// It was forty pixels tall whatever it was carrying, which is two lines at one font size and one
/// and a half at the next — so "Open in Todoist" was cut in half and the way out of the filter went
/// with it. What it takes is measured now, and these are about the measuring.
/// </remarks>
public class UnsupportedNoticeTests
{
    private static readonly Padding Room = new(8, 4, 8, 4);

    private static Font Face() => new("Segoe UI", 9f);

    [Fact]
    public void Two_lines_are_taller_than_one()
    {
        // The notice is always two: what it can't read, and the offer to open it elsewhere. The
        // fixed height had room for one and a bit of the second.
        using var font = Face();

        var one = MainForm.NoticeHeight("Termyn can't read this filter: :to_me:", font, 800, Room);
        var two = MainForm.NoticeHeight("Termyn can't read this filter: :to_me:\nOpen in Todoist", font, 800, Room);

        Assert.True(two > one, $"two lines measured {two}, one measured {one}");
    }

    [Fact]
    public void A_long_query_takes_more_room_in_a_narrow_window()
    {
        // A query comes off the account and is only cut down at eighty characters, which is wider
        // than the sidebar leaves at a small window. Wrapped, it needs the extra line.
        using var font = Face();
        var text = $"Termyn can't read this filter: {new string('w', 80)}\nOpen in Todoist";

        var wide = MainForm.NoticeHeight(text, font, 1600, Room);
        var narrow = MainForm.NoticeHeight(text, font, 260, Room);

        Assert.True(narrow > wide, $"narrow measured {narrow}, wide measured {wide}");
    }

    [Fact]
    public void A_bigger_font_needs_more_height_for_the_same_words()
    {
        // Which is how the fixed height came to be wrong: it was two lines at the size it was
        // written against, and everyone running a larger system font lost the link.
        using var small = new Font("Segoe UI", 9f);
        using var large = new Font("Segoe UI", 16f);

        const string text = "Termyn can't read this filter: :to_me:\nOpen in Todoist";

        Assert.True(
            MainForm.NoticeHeight(text, large, 800, Room) > MainForm.NoticeHeight(text, small, 800, Room));
    }

    [Fact]
    public void A_strip_that_has_not_been_laid_out_yet_still_measures()
    {
        // Width zero, which is what a control is before its first layout. Subtracting the padding
        // would take the wrap width negative, and a negative wrap puts every word on its own line.
        using var font = Face();

        var height = MainForm.NoticeHeight("Termyn can't read this filter: :to_me:\nOpen in Todoist", font, 0, Room);

        Assert.InRange(height, 1, 400);
    }
}
