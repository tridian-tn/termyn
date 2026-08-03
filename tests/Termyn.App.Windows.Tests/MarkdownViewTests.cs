using Termyn.Core.Settings;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The rendered half of the notes panel. Styling a run means selecting it, and a selection needs a
/// window behind it — so each of these realises the control without ever showing it.
/// </summary>
public class MarkdownViewTests
{
    private static MarkdownView Render(string markdown)
    {
        var view = new MarkdownView { Theme = Theme.Resolve(ThemePreference.Light) };
        view.CreateControl();
        view.Markdown = markdown;
        return view;
    }

    /// <summary>The font a run of the rendered text is drawn in.</summary>
    private static Font FontAt(MarkdownView view, string needle)
    {
        var at = view.Text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{needle}' is not in the rendered text: {view.Text}");

        view.SelectionStart = at;
        view.SelectionLength = needle.Length;
        return view.SelectionFont!;
    }

    private static Color ColourAt(MarkdownView view, string needle)
    {
        FontAt(view, needle);
        return view.SelectionColor;
    }

    // ---- What it reads as ----------------------------------------------------------------------

    [Fact]
    public void The_markers_are_drawn_rather_than_shown()
    {
        using var view = Render("Some **bold** and some *italic* here");

        Assert.Equal("Some bold and some italic here", view.Text.Trim());
    }

    [Fact]
    public void Bold_is_bold_and_italic_is_italic()
    {
        using var view = Render("Some **bold** and some *italic* here");

        Assert.True(FontAt(view, "bold").Bold);
        Assert.False(FontAt(view, "bold").Italic);
        Assert.True(FontAt(view, "italic").Italic);
        Assert.False(FontAt(view, "italic").Bold);
        Assert.False(FontAt(view, "Some").Bold);
    }

    [Fact]
    public void Strikethrough_is_struck_through()
    {
        // Todoist's own editor writes this one with two tildes, which plain markdown has no syntax
        // for at all — so the parser has to be told to read it.
        using var view = Render("This is ~~gone~~ now");

        Assert.True(FontAt(view, "gone").Strikeout);
        Assert.Equal("This is gone now", view.Text.Trim());
    }

    [Fact]
    public void A_heading_is_larger_and_bold()
    {
        using var view = Render("# A heading\n\nSome text");

        var heading = FontAt(view, "A heading");
        var body = FontAt(view, "Some text");

        Assert.True(heading.Bold);
        Assert.True(heading.Size > body.Size);
        Assert.Equal("A heading", view.Text.Split('\n')[0].Trim());
    }

    [Fact]
    public void A_bullet_list_gets_bullets_and_an_indent()
    {
        using var view = Render("- first\n- second");

        Assert.Contains("•", view.Text);
        Assert.Contains("first", view.Text);
        Assert.Contains("second", view.Text);

        // Indented as a list rather than run together as one paragraph.
        FontAt(view, "first");
        Assert.True(view.SelectionIndent > 0 || view.SelectionHangingIndent > 0);
    }

    [Fact]
    public void A_numbered_list_keeps_its_numbers()
    {
        using var view = Render("1. first\n2. second");

        Assert.Contains("1.", view.Text);
        Assert.Contains("2.", view.Text);
    }

    [Fact]
    public void A_link_shows_its_words_and_not_its_address()
    {
        // A description pasted off a web page is mostly link text, and printing every target
        // alongside it would drown the thing being read.
        using var view = Render("See [the docs](https://example.com/very/long/path) for more");

        Assert.Equal("See the docs for more", view.Text.Trim());
        Assert.DoesNotContain("example.com", view.Text);
    }

    [Fact]
    public void A_link_is_coloured_apart_from_the_words_around_it()
    {
        var theme = Theme.Resolve(ThemePreference.Light);
        using var view = Render("See [the docs](https://example.com) for more");

        Assert.Equal(theme.Accent, ColourAt(view, "the docs"));
        Assert.NotEqual(theme.Accent, ColourAt(view, "See"));
    }

    [Fact]
    public void A_link_can_be_followed_from_the_words_it_is_on()
    {
        // Colour alone said nothing you could act on. The address is kept against the span the
        // words occupy, because by the time it is on screen there is nothing else left to open.
        using var view = Render("See [the docs](https://example.com/path) for more");

        var at = view.Text.IndexOf("the docs", StringComparison.Ordinal);

        Assert.Equal("https://example.com/path", view.LinkAt(at));
        Assert.Equal("https://example.com/path", view.LinkAt(at + "the docs".Length - 1));
    }

    [Fact]
    public void The_words_either_side_of_a_link_are_not_part_of_it()
    {
        using var view = Render("See [the docs](https://example.com) for more");

        Assert.Null(view.LinkAt(view.Text.IndexOf("See", StringComparison.Ordinal)));
        Assert.Null(view.LinkAt(view.Text.IndexOf("for more", StringComparison.Ordinal)));
    }

    [Fact]
    public void Several_links_each_keep_their_own_address()
    {
        using var view = Render("[first](https://one.example) and [second](https://two.example)");

        Assert.Equal("https://one.example/", view.LinkAt(view.Text.IndexOf("first", StringComparison.Ordinal)));
        Assert.Equal("https://two.example/", view.LinkAt(view.Text.IndexOf("second", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_link_that_is_not_a_web_address_is_not_offered_as_one()
    {
        // A description syncs from an account and gets pasted into from anywhere. A scheme that
        // means "open this document" or "run this" is not something a note gets to ask for.
        using var view = Render("[a file](file:///C:/Windows/System32/cmd.exe) and [a script](javascript:alert(1))");

        Assert.Null(view.LinkAt(view.Text.IndexOf("a file", StringComparison.Ordinal)));
        Assert.Null(view.LinkAt(view.Text.IndexOf("a script", StringComparison.Ordinal)));

        // Still readable — it just isn't clickable.
        Assert.Contains("a file", view.Text);
    }

    [Fact]
    public void The_links_from_the_last_task_do_not_linger()
    {
        using var view = Render("[first task](https://one.example)");

        view.Markdown = "The second task, with nothing to click";

        Assert.Null(view.LinkAt(0));
    }

    [Fact]
    public void Code_is_set_in_a_fixed_width_face()
    {
        using var view = Render("Run `dotnet build` first");

        Assert.Equal(FontFamily.GenericMonospace.Name, FontAt(view, "dotnet build").FontFamily.Name);
        Assert.Equal("Run dotnet build first", view.Text.Trim());
    }

    [Fact]
    public void A_fenced_block_keeps_its_lines()
    {
        using var view = Render("```\nfirst line\nsecond line\n```");

        Assert.Contains("first line", view.Text);
        Assert.Contains("second line", view.Text);
        Assert.DoesNotContain("```", view.Text);
    }

    [Fact]
    public void A_bare_url_is_shown_as_it_was_typed()
    {
        // People paste these far more often than they write proper links.
        using var view = Render("See https://example.com for more");

        Assert.Contains("https://example.com", view.Text);
    }

    // ---- Not falling over ----------------------------------------------------------------------

    [Fact]
    public void Nothing_at_all_renders_to_nothing_at_all()
    {
        using var view = Render(string.Empty);

        Assert.Equal(string.Empty, view.Text.Trim());
    }

    [Fact]
    public void Plain_text_with_no_markdown_in_it_comes_through_unchanged()
    {
        using var view = Render("Just a sentence, with a comma and a full stop.");

        Assert.Equal("Just a sentence, with a comma and a full stop.", view.Text.Trim());
    }

    [Fact]
    public void Markdown_it_has_no_way_to_draw_still_shows_its_words()
    {
        // A table is beyond what Todoist's editor can produce, but not beyond what someone can
        // paste. It doesn't have to be drawn as a table; it does have to be readable.
        using var view = Render("| a | b |\n| - | - |\n| 1 | 2 |\n\nAfter the table");

        Assert.Contains("After the table", view.Text);
    }

    [Fact]
    public void Text_set_before_there_was_a_window_is_drawn_once_there_is_one()
    {
        // The panel starts collapsed, so the first description usually arrives before this control
        // has a window of its own.
        using var view = new MarkdownView { Theme = Theme.Resolve(ThemePreference.Light) };
        view.Markdown = "Some **bold** text";

        Assert.Equal(string.Empty, view.Text);

        view.CreateControl();

        Assert.Equal("Some bold text", view.Text.Trim());
    }

    [Fact]
    public void Moving_to_another_task_replaces_what_was_there()
    {
        using var view = Render("The first task's notes");

        view.Markdown = "The second task's notes";

        Assert.DoesNotContain("first", view.Text);
        Assert.Contains("second", view.Text);
    }

    [Fact]
    public void The_box_is_read_only()
    {
        // The account's text is what gets saved. A rendering that could be edited would have to be
        // serialised back to markdown, and that is where formatting quietly goes missing.
        using var view = Render("Anything");

        Assert.True(view.ReadOnly);
    }
}
