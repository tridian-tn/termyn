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

    /// <summary>
    /// The font a run of the rendered text is drawn in.
    /// </summary>
    /// <remarks>
    /// Everything it can answer wrongly says what it saw, because one of these failed once on a
    /// build agent and could not be made to fail again — and "Assert.True() Failure" with nothing
    /// after it is a report nobody can act on. A selection spanning more than one font answers null
    /// rather than a font, which would otherwise arrive as a null reference from somewhere further
    /// down and say even less.
    /// </remarks>
    private static Font FontAt(MarkdownView view, string needle)
    {
        var at = view.Text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{needle}' is not in the rendered text: '{view.Text}'");

        view.SelectionStart = at;
        view.SelectionLength = needle.Length;

        var font = view.SelectionFont;
        Assert.True(
            font is not null,
            $"'{needle}' at {at} is drawn in more than one font, so there is no single answer. "
            + $"Rendered text: '{view.Text}'");

        return font!;
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
        // Every assertion here says what it saw. This is the one that failed on a build agent and
        // then passed on a re-run of the same commit, reporting nothing but "Assert.True() Failure"
        // — which narrowed it to one of two lines and told us nothing about either. See #42.
        using var view = Render("Some **bold** and some *italic* here");

        // Read once each, before anything is asserted. A message argument is built whether or not
        // the assertion fails, so asking the control again inside it would double the selections
        // this test makes — on a test being instrumented precisely because something about it is
        // occasionally not reproducible — and would let the message describe a different look at
        // the control from the one that failed.
        var text = view.Text.Trim();
        var bold = FontAt(view, "bold");
        var italic = FontAt(view, "italic");
        var plain = FontAt(view, "Some");

        Assert.True(bold.Bold, Drawn("bold", bold, text));
        Assert.False(bold.Italic, Drawn("bold", bold, text));
        Assert.True(italic.Italic, Drawn("italic", italic, text));
        Assert.False(italic.Bold, Drawn("italic", italic, text));
        Assert.False(plain.Bold, Drawn("Some", plain, text));
    }

    /// <summary>How a word actually came out, for an assertion that is about to say it is wrong.</summary>
    private static string Drawn(string needle, Font font, string text)
        => $"'{needle}' is {font.FontFamily.Name} {font.Size}pt {font.Style}. Rendered text: '{text}'";

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
    public void A_links_colour_stops_where_its_words_do()
    {
        // The line ending after a link draws nothing, so a link-coloured one is invisible here —
        // but it goes out with the text when a selection spanning it is copied somewhere that
        // keeps formatting, and it makes a nonsense of asking what colour the line is.
        var theme = Theme.Resolve(ThemePreference.Light);
        using var view = Render("A line ending in [a link](https://example.com)");

        view.SelectionStart = view.Text.IndexOf("a link", StringComparison.Ordinal) + "a link".Length;
        view.SelectionLength = view.TextLength - view.SelectionStart;

        Assert.NotEqual(theme.Accent, view.SelectionColor);
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

        // Asserted as a link, not just as text: Markdig writes the URL out either way, so without
        // this the test passes with the autolink extension taken out of the pipeline.
        Assert.Equal("https://example.com/", view.LinkAt(view.Text.IndexOf("https://example.com", StringComparison.Ordinal)));
    }

    // ---- Not falling over ----------------------------------------------------------------------

    // ---- What used to vanish, and what used to throw -------------------------------------------

    [Fact]
    public void Markdown_nested_past_what_the_parser_will_take_still_shows_its_words()
    {
        // The parser refuses this by throwing, and a description arrives by sync — so a note
        // written on another device could take the window down on the next publish, with the box
        // that would let you fix it being the thing that threw.
        using var view = Render(new string('>', 200) + " still here");

        Assert.Contains("still here", view.Text);
    }

    [Fact]
    public void A_list_nested_past_what_the_parser_will_take_still_shows_its_words()
    {
        // Lists give out sooner than quotes do — depth sixty-four rather than a hundred and
        // twenty-eight — so this is the one a pasted outline reaches first.
        var deep = string.Concat(Enumerable.Range(0, 80).Select(i => new string(' ', i * 2) + "- level" + Environment.NewLine));

        using var view = Render(deep);

        Assert.Contains("level", view.Text);
    }

    [Fact]
    public void A_pasted_block_of_html_shows_its_words_rather_than_disappearing()
    {
        // A leaf rather than a container, so it matched nothing and its text was dropped whole —
        // and a blank preview reads as a task with no notes on it.
        using var view = Render("<div class=\"x\">something worth reading</div>\n\nand after it");

        Assert.Contains("something worth reading", view.Text);
        Assert.Contains("and after it", view.Text);
    }

    [Fact]
    public void An_angle_bracketed_link_is_shown_and_can_be_followed()
    {
        // The form markdown copied out of docs and READMEs uses. It was rendering as nothing at
        // all: no words, no link, no sign there had been a URL there.
        using var view = Render("See <https://example.com/x> for more");

        Assert.Contains("https://example.com/x", view.Text);
        Assert.Equal("https://example.com/x", view.LinkAt(view.Text.IndexOf("https://example.com/x", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_email_in_angle_brackets_is_shown_but_not_offered_as_a_link()
    {
        using var view = Render("Mail <bob@example.com> about it");

        Assert.Contains("bob@example.com", view.Text);
        Assert.Null(view.LinkAt(view.Text.IndexOf("bob@example.com", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_escaped_character_is_shown_as_the_character()
    {
        // Anything that generates markdown out of HTML writes ampersands this way, and they were
        // going missing mid-sentence.
        using var view = Render("Tom &amp; Jerry");

        Assert.Contains("Tom & Jerry", view.Text);
        Assert.DoesNotContain("&amp;", view.Text);
    }

    [Fact]
    public void A_link_that_is_not_a_web_address_is_not_coloured_as_one_either()
    {
        // It isn't clickable, so it shouldn't look clickable. Drawn in the link colour it invites
        // a click that then does nothing at all.
        var theme = Theme.Resolve(ThemePreference.Light);
        using var view = Render("[a file](file:///C:/Windows/System32/cmd.exe)");

        Assert.Equal("a file", view.Text.Trim());
        Assert.NotEqual(theme.Accent, ColourAt(view, "a file"));
    }

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

    // ---- The rest of the grammar ---------------------------------------------------------------

    [Fact]
    public void A_checklist_keeps_its_boxes_ticked_and_unticked()
    {
        // The description shape Todoist users write most.
        using var view = Render("- [x] done\n- [ ] still to do");

        Assert.Contains("[x]", view.Text);
        Assert.Contains("[ ]", view.Text);
        Assert.Contains("still to do", view.Text);
    }

    [Fact]
    public void One_newline_joins_its_lines_and_two_spaces_break_them()
    {
        // The commonest shape of a real description — people press Enter once — so whichever way
        // it goes is worth writing down.
        using var joined = Render("line one\nline two");
        using var broken = Render("line one  \nline two");

        Assert.Equal("line one line two", joined.Text.Trim());
        Assert.Equal(2, broken.Text.Trim().Split('\n').Length);
    }

    [Fact]
    public void Each_heading_level_is_smaller_than_the_one_above_it()
    {
        using var view = Render("# one\n\n## two\n\n### three\n\nbody");

        var one = FontAt(view, "one").Size;
        var two = FontAt(view, "two").Size;
        var three = FontAt(view, "three").Size;
        var body = FontAt(view, "body").Size;

        Assert.True(one > two, $"h1 {one} should beat h2 {two}");
        Assert.True(two > three, $"h2 {two} should beat h3 {three}");
        Assert.True(three > body, $"h3 {three} should beat body {body}");
    }

    [Fact]
    public void A_nested_list_sits_in_from_the_one_it_belongs_to()
    {
        using var view = Render("- outer\n    - inner");

        FontAt(view, "outer");
        var outer = view.SelectionIndent;
        FontAt(view, "inner");

        Assert.True(view.SelectionIndent > outer, $"inner {view.SelectionIndent} should sit in from outer {outer}");
    }

    [Fact]
    public void A_quote_sits_in_from_the_text_around_it()
    {
        // Its marker is dropped, so the indent is the only thing that says it is a quotation.
        using var view = Render("before\n\n> quoted\n\nafter");

        FontAt(view, "before");
        var body = view.SelectionIndent;
        FontAt(view, "quoted");

        Assert.True(view.SelectionIndent > body, $"quote {view.SelectionIndent} should sit in from body {body}");
    }

    [Fact]
    public void A_numbered_list_starts_where_it_says_it_does()
    {
        using var view = Render("3. third\n4. fourth");

        Assert.Contains("3.", view.Text);
        Assert.Contains("4.", view.Text);
    }

    [Fact]
    public void A_link_with_no_words_is_not_a_link_at_all()
    {
        // A zero-width span would make whatever follows it clickable.
        using var view = Render("[](https://example.com) after");

        Assert.Null(view.LinkAt(0));
    }

    [Fact]
    public void A_rule_is_drawn_between_what_it_divides()
    {
        using var view = Render("above\n\n---\n\nbelow");

        Assert.Contains("above", view.Text);
        Assert.Contains("below", view.Text);
        Assert.Contains('—', view.Text);
    }

    [Fact]
    public void Changing_the_theme_redraws_what_is_already_on_screen()
    {
        // The only thing that recolours the panel when the app switches theme.
        using var view = Render("See [the docs](https://example.com) now");

        view.Theme = Theme.Resolve(ThemePreference.Dark);

        Assert.Equal(Theme.Resolve(ThemePreference.Dark).Accent, ColourAt(view, "the docs"));
        Assert.Equal("https://example.com/", view.LinkAt(view.Text.IndexOf("the docs", StringComparison.Ordinal)));
    }

    // ---- Finding the way back to the markdown ---------------------------------------------------

    /// <summary>Where the markdown behind the rendered word <paramref name="needle"/> starts.</summary>
    private static int SourceOf(MarkdownView view, string needle)
    {
        var at = view.Text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{needle}' is not in the rendered text: {view.Text}");
        return view.SourceAt(at);
    }

    [Fact]
    public void A_word_in_the_rendering_knows_where_it_was_written()
    {
        // What puts the caret where the user was pointing when they ask to type. Without it the
        // only honest answer is the top of the description, and a click halfway down a note is
        // then a click that scrolls you away from what you were reading.
        const string markdown = "Some **bold** text";
        using var view = Render(markdown);

        Assert.Equal(markdown.IndexOf("bold", StringComparison.Ordinal), SourceOf(view, "bold"));
        Assert.Equal(markdown.IndexOf("Some", StringComparison.Ordinal), SourceOf(view, "Some"));
        Assert.Equal(markdown.IndexOf("text", StringComparison.Ordinal), SourceOf(view, "text"));
    }

    [Fact]
    public void An_offset_inside_a_word_maps_through_it_rather_than_to_its_start()
    {
        const string markdown = "abcdefgh";
        using var view = Render(markdown);

        Assert.Equal(0, view.SourceAt(0));
        Assert.Equal(3, view.SourceAt(3));
        Assert.Equal(7, view.SourceAt(7));
    }

    [Fact]
    public void A_line_below_the_first_maps_past_the_lines_above_it()
    {
        // The rendering drops markers, so the two texts drift apart as they go — which is the whole
        // reason the offset can't just be carried across.
        const string markdown = "# A heading\n\nThe *body* of it";
        using var view = Render(markdown);

        Assert.Equal(markdown.IndexOf("body", StringComparison.Ordinal), SourceOf(view, "body"));
    }

    [Fact]
    public void The_words_of_a_link_map_to_the_words_and_not_to_the_address()
    {
        // The address isn't drawn at all, so an offset that landed in it would put the caret
        // somewhere the user never saw.
        const string markdown = "See [the docs](https://example.com/path) for more";
        using var view = Render(markdown);

        Assert.Equal(markdown.IndexOf("the docs", StringComparison.Ordinal), SourceOf(view, "the docs"));
    }

    [Fact]
    public void Code_maps_to_the_code_and_not_to_the_backtick_in_front_of_it()
    {
        // The backticks are written and not drawn, so mapping the whole span would put the first
        // character of what is on screen onto the marker before it — and every offset into the run
        // one short of where it was aimed.
        const string markdown = "Run `dotnet build` first";
        using var view = Render(markdown);

        Assert.Equal(markdown.IndexOf("dotnet", StringComparison.Ordinal), SourceOf(view, "dotnet"));
    }

    [Fact]
    public void An_angle_bracketed_url_maps_to_the_url_and_not_to_the_bracket()
    {
        const string markdown = "See <https://example.com/x> for more";
        using var view = Render(markdown);

        Assert.Equal(markdown.IndexOf("https", StringComparison.Ordinal), SourceOf(view, "https"));
    }

    [Fact]
    public void A_bullet_maps_to_the_item_it_marks_rather_than_to_the_line_before_it()
    {
        // The marker is drawn rather than written, so it belongs to no run of the markdown. Landing
        // on the text it introduces is what a click on it was aiming at; landing at the end of the
        // previous line is the caret going backwards from where the user pointed.
        const string markdown = "before\n\n- the item";
        using var view = Render(markdown);

        var bullet = view.Text.IndexOf('•');

        Assert.True(bullet >= 0, $"no bullet in: {view.Text}");
        Assert.Equal(markdown.IndexOf("the item", StringComparison.Ordinal), view.SourceAt(bullet));
    }

    [Fact]
    public void An_offset_past_everything_lands_at_the_end_of_the_markdown()
    {
        // Clicking in the empty space below a short description. The end is where a caret goes when
        // there is nothing under the pointer, since that is where more of it would be written.
        const string markdown = "a short note";
        using var view = Render(markdown);

        Assert.Equal(markdown.Length, view.SourceAt(view.TextLength + 500));
    }

    [Fact]
    public void Nothing_at_all_maps_to_the_start()
    {
        using var view = Render(string.Empty);

        Assert.Equal(0, view.SourceAt(0));
        Assert.Equal(0, view.SourceAt(40));
    }

    [Fact]
    public void The_offsets_from_the_last_task_do_not_linger()
    {
        // Same failure the links had: a map left over from the task before points the caret into a
        // description that is no longer on screen.
        using var view = Render("a much longer first description than the one that follows it");

        view.Markdown = "short";

        Assert.Equal("short".Length, view.SourceAt(500));
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
