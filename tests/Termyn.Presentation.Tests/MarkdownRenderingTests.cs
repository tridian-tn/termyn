using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

/// <summary>
/// A description as it reads: the runs it's drawn as, and the way back from them to the markdown.
/// </summary>
/// <remarks>
/// Asked of the run list rather than of a control, so none of these needs a window. That the box
/// draws each run the way its style says — the fonts, the colours, the indents, the air — and holds
/// exactly as much text as the rendering counts is the control's half, and tested with it in
/// <c>MarkdownViewTests</c>.
/// </remarks>
public class MarkdownRenderingTests
{
    private static MarkdownRendering Render(string markdown) => MarkdownRendering.Of(markdown);

    /// <summary>The run holding some words.</summary>
    private static RenderedRun RunOf(MarkdownRendering rendering, string needle)
    {
        var run = rendering.Runs.FirstOrDefault(r => r.Text.Contains(needle, StringComparison.Ordinal));
        Assert.True(run is not null, $"'{needle}' is in no run. Rendered: '{Shown(rendering)}'");
        return run!;
    }

    /// <summary>The rendering, with its line endings spelled out, for a message.</summary>
    private static string Shown(MarkdownRendering rendering) => rendering.Text.Replace("\n", "\\n", StringComparison.Ordinal);

    // ---- What it reads as ----------------------------------------------------------------------

    [Fact]
    public void The_markers_are_drawn_rather_than_shown()
        => Assert.Equal("Some bold and some italic here", Render("Some **bold** and some *italic* here").Text.Trim());

    [Fact]
    public void Bold_is_bold_and_italic_is_italic()
    {
        var rendering = Render("Some **bold** and some *italic* here");

        Assert.Equal(new RenderedStyle(Bold: true), RunOf(rendering, "bold").Style);
        Assert.Equal(new RenderedStyle(Italic: true), RunOf(rendering, "italic").Style);
        Assert.Equal(RenderedStyle.Plain, RunOf(rendering, "Some").Style);
    }

    [Fact]
    public void Strikethrough_is_struck_through()
    {
        // Todoist's own editor writes this one with two tildes, which plain markdown has no syntax
        // for at all — so the parser has to be told to read it.
        var rendering = Render("This is ~~gone~~ now");

        Assert.True(RunOf(rendering, "gone").Style.Strike);
        Assert.Equal("This is gone now", rendering.Text.Trim());
    }

    [Fact]
    public void Each_heading_level_is_bold_and_smaller_than_the_one_above_it()
    {
        var rendering = Render("# one\n\n## two\n\n### three\n\nbody");

        var one = RunOf(rendering, "one").Style;
        var two = RunOf(rendering, "two").Style;
        var three = RunOf(rendering, "three").Style;

        Assert.True(one.Bold && two.Bold && three.Bold);
        Assert.True(one.Larger > two.Larger && two.Larger > three.Larger && three.Larger > RunOf(rendering, "body").Style.Larger);
    }

    [Fact]
    public void A_bullet_leads_its_item_and_the_words_hang_in_from_it()
    {
        // The bullet starts the line at the margin and everything after it, wrapped lines included,
        // sits in from it — so every run of the item shares the one hanging paragraph.
        var rendering = Render("- first\n- second");

        var bullet = rendering.Runs.First(r => r.Text.StartsWith('•'));
        var words = RunOf(rendering, "first");

        Assert.Equal(new RenderedStyle(Muted: true, Hanging: true, Tight: true), bullet.Style);
        Assert.Equal(new RenderedStyle(Hanging: true, Tight: true), words.Style);
    }

    [Fact]
    public void A_nested_list_sits_in_from_the_one_it_belongs_to()
    {
        var rendering = Render("- outer\n    - inner");

        Assert.True(RunOf(rendering, "inner").Style.Indent > RunOf(rendering, "outer").Style.Indent);
    }

    [Fact]
    public void A_quote_sits_in_from_the_text_around_it()
    {
        // Its marker is dropped, so the indent is the only thing that says it is a quotation.
        var rendering = Render("before\n\n> quoted\n\nafter");

        Assert.True(RunOf(rendering, "quoted").Style.Indent > RunOf(rendering, "before").Style.Indent);
    }

    [Fact]
    public void Code_is_set_apart_in_a_fixed_width_face()
    {
        var rendering = Render("Run `dotnet build` first");

        Assert.Equal(new RenderedStyle(Fixed: true, Muted: true), RunOf(rendering, "dotnet build").Style);
        Assert.Equal("Run dotnet build first", rendering.Text.Trim());
    }

    [Fact]
    public void A_numbered_list_keeps_its_numbers()
    {
        var text = Render("1. first\n2. second").Text;

        Assert.Contains("1.", text);
        Assert.Contains("2.", text);
    }

    [Fact]
    public void A_numbered_list_starts_where_it_says_it_does()
    {
        var text = Render("3. third\n4. fourth").Text;

        Assert.Contains("3.", text);
        Assert.Contains("4.", text);
    }

    [Fact]
    public void A_fenced_block_keeps_its_lines()
    {
        var text = Render("```\nfirst line\nsecond line\n```").Text;

        Assert.Contains("first line", text);
        Assert.Contains("second line", text);
        Assert.DoesNotContain("```", text);
    }

    [Theory]
    [InlineData("```")]
    [InlineData("```js")]
    [InlineData("~~~")]
    [InlineData("```\n```")]
    [InlineData("Some notes\n\n```")]
    public void A_fence_with_nothing_in_it_is_not_the_end_of_the_description(string markdown)
    {
        // Typed as the start of a code block and left there for a moment, which is all it takes for
        // the rendering to be drawn from it. A fence with no lines under it used to throw rather than
        // draw nothing.
        var rendering = Render(markdown);

        Assert.Equal(rendering.Text.Length, rendering.Length);
        Assert.DoesNotContain("```", rendering.Text);
    }

    [Fact]
    public void A_checklist_keeps_its_boxes_ticked_and_unticked()
    {
        // The description shape Todoist users write most.
        var text = Render("- [x] done\n- [ ] still to do").Text;

        Assert.Contains("[x]", text);
        Assert.Contains("[ ]", text);
        Assert.Contains("still to do", text);
    }

    [Fact]
    public void A_rule_is_drawn_between_what_it_divides()
    {
        var text = Render("above\n\n---\n\nbelow").Text;

        Assert.Contains("above", text);
        Assert.Contains("below", text);
        Assert.Contains('—', text);
    }

    [Fact]
    public void Nothing_at_all_renders_to_nothing_at_all()
    {
        var rendering = Render(string.Empty);

        Assert.Equal(string.Empty, rendering.Text);
        Assert.Equal(0, rendering.Length);
    }

    [Fact]
    public void Plain_text_with_no_markdown_in_it_comes_through_unchanged()
        => Assert.Equal("Just a sentence, with a comma and a full stop.", Render("Just a sentence, with a comma and a full stop.").Text.Trim());

    // ---- Links ---------------------------------------------------------------------------------

    [Fact]
    public void A_link_shows_its_words_and_not_its_address()
    {
        // A description pasted off a web page is mostly link text, and printing every target
        // alongside it would drown the thing being read.
        var text = Render("See [the docs](https://example.com/very/long/path) for more").Text;

        Assert.Equal("See the docs for more", text.Trim());
        Assert.DoesNotContain("example.com", text);
    }

    [Fact]
    public void A_link_can_be_followed_from_the_words_it_is_on()
    {
        // The address is kept against the words, because once they're drawn there is nothing else
        // left to open.
        var rendering = Render("See [the docs](https://example.com/path) for more");
        var at = rendering.Text.IndexOf("the docs", StringComparison.Ordinal);

        Assert.Equal("https://example.com/path", rendering.LinkAt(at));
        Assert.Equal("https://example.com/path", rendering.LinkAt(at + "the docs".Length - 1));
    }

    [Fact]
    public void The_words_either_side_of_a_link_are_not_part_of_it()
    {
        var rendering = Render("See [the docs](https://example.com) for more");

        Assert.Null(rendering.LinkAt(rendering.Text.IndexOf("See", StringComparison.Ordinal)));
        Assert.Null(rendering.LinkAt(rendering.Text.IndexOf("for more", StringComparison.Ordinal)));
    }

    [Fact]
    public void Several_links_each_keep_their_own_address()
    {
        var rendering = Render("[first](https://one.example) and [second](https://two.example)");

        Assert.Equal("https://one.example/", rendering.LinkAt(rendering.Text.IndexOf("first", StringComparison.Ordinal)));
        Assert.Equal("https://two.example/", rendering.LinkAt(rendering.Text.IndexOf("second", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_link_that_is_not_a_web_address_is_not_offered_as_one()
    {
        // A description syncs from an account and gets pasted into from anywhere. A scheme that
        // means "open this document" or "run this" is not something a description gets to ask for.
        var rendering = Render("[a file](file:///C:/Windows/System32/cmd.exe) and [a script](javascript:alert(1))");

        Assert.Null(rendering.LinkAt(rendering.Text.IndexOf("a file", StringComparison.Ordinal)));
        Assert.Null(rendering.LinkAt(rendering.Text.IndexOf("a script", StringComparison.Ordinal)));
        Assert.Null(RunOf(rendering, "a file").Link);

        // Still readable — it just isn't clickable.
        Assert.Contains("a file", rendering.Text);
    }

    [Fact]
    public void Formatting_inside_a_link_is_still_part_of_the_link()
    {
        var rendering = Render("See [the **bold** docs](https://example.com) now");

        Assert.Equal("https://example.com/", RunOf(rendering, "bold").Link);
        Assert.True(RunOf(rendering, "bold").Style.Bold);
        Assert.Null(RunOf(rendering, "now").Link);
    }

    [Fact]
    public void A_link_with_no_words_is_not_a_link_at_all()
    {
        // A zero-width span would make whatever follows it clickable.
        Assert.Null(Render("[](https://example.com) after").LinkAt(0));
    }

    [Fact]
    public void A_bare_url_is_shown_as_it_was_typed_and_can_be_followed()
    {
        // People paste these far more often than they write proper links. Asserted as a link, not
        // just as text: Markdig writes the URL out either way, so without that this passes with the
        // autolink extension taken out of the pipeline.
        var rendering = Render("See https://example.com for more");

        Assert.Contains("https://example.com", rendering.Text);
        Assert.Equal("https://example.com/", rendering.LinkAt(rendering.Text.IndexOf("https://example.com", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_angle_bracketed_link_is_shown_and_can_be_followed()
    {
        // The form markdown copied out of docs and READMEs uses. It was rendering as nothing at
        // all: no words, no link, no sign there had been a URL there.
        var rendering = Render("See <https://example.com/x> for more");

        Assert.Contains("https://example.com/x", rendering.Text);
        Assert.Equal("https://example.com/x", rendering.LinkAt(rendering.Text.IndexOf("https://example.com/x", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_email_in_angle_brackets_is_shown_but_not_offered_as_a_link()
    {
        var rendering = Render("Mail <bob@example.com> about it");

        Assert.Contains("bob@example.com", rendering.Text);
        Assert.Null(rendering.LinkAt(rendering.Text.IndexOf("bob@example.com", StringComparison.Ordinal)));
    }

    // ---- What used to vanish, and what used to throw -------------------------------------------

    [Fact]
    public void Markdown_nested_past_what_the_parser_will_take_still_shows_its_words()
    {
        // The parser refuses this by throwing, and a description arrives by sync — so a description
        // written on another device could take the window down on the next publish.
        Assert.Contains("still here", Render(new string('>', 200) + " still here").Text);
    }

    [Fact]
    public void A_list_nested_past_what_the_parser_will_take_still_shows_its_words()
    {
        // Lists give out sooner than quotes do — depth sixty-four rather than a hundred and
        // twenty-eight — so this is the one a pasted outline reaches first.
        var deep = string.Concat(Enumerable.Range(0, 80).Select(i => new string(' ', i * 2) + "- level" + Environment.NewLine));

        Assert.Contains("level", Render(deep).Text);
    }

    [Fact]
    public void A_pasted_block_of_html_shows_its_words_rather_than_disappearing()
    {
        // A leaf rather than a container, so it matched nothing and its text was dropped whole.
        var text = Render("<div class=\"x\">something worth reading</div>\n\nand after it").Text;

        Assert.Contains("something worth reading", text);
        Assert.Contains("and after it", text);
    }

    [Fact]
    public void An_escaped_character_is_shown_as_the_character()
    {
        // Anything that generates markdown out of HTML writes ampersands this way, and they were
        // going missing mid-sentence.
        var text = Render("Tom &amp; Jerry").Text;

        Assert.Contains("Tom & Jerry", text);
        Assert.DoesNotContain("&amp;", text);
    }

    [Fact]
    public void Markdown_it_has_no_way_to_draw_still_shows_its_words()
    {
        // A table is beyond what Todoist's editor can produce, but not beyond what someone can
        // paste. It doesn't have to be drawn as a table; it does have to be readable.
        Assert.Contains("After the table", Render("| a | b |\n| - | - |\n| 1 | 2 |\n\nAfter the table").Text);
    }

    // ---- Line breaks ----------------------------------------------------------------------------

    /// <summary>The rendering as lines, without the blank the last line ending leaves.</summary>
    private static string[] Lines(string markdown) => Render(markdown).Text.TrimEnd('\n').Split('\n');

    [Fact]
    public void A_line_typed_on_its_own_is_drawn_on_its_own()
    {
        // Markdown proper would run these together with a space between them. Todoist breaks the
        // line on a single Return and the descriptions in an account are written that way.
        var lines = Lines("Azure.Storage.Blobs = 12.17.0\nAzure.Messaging.ServiceBus = 7.15.0");

        Assert.Equal(2, lines.Length);
        Assert.Equal("Azure.Storage.Blobs = 12.17.0", lines[0]);
    }

    [Fact]
    public void A_blank_line_still_starts_a_new_paragraph_rather_than_a_third_line()
        => Assert.Equal(["First thought", "Second thought"], Lines("First thought\n\nSecond thought"));

    [Fact]
    public void A_break_written_the_markdown_way_still_breaks_and_does_not_double()
        => Assert.Equal(["First line", "Second line"], Lines("First line  \nSecond line"));

    [Fact]
    public void A_run_of_blank_lines_reads_as_one_break_and_not_as_several()
        => Assert.Equal(["One", "Two"], Lines("One\n\n\nTwo"));

    [Fact]
    public void A_paragraph_ends_with_air_under_it_and_a_line_broken_inside_one_does_not()
    {
        // The difference between a new line and a new thought. The line ending inside the
        // paragraph is tight; the one that ends it isn't; a list's items are tight throughout.
        var rendering = Render("First line\nSecond line\n\nNext thought\n\n- an item\n- another");
        var endings = rendering.Runs.Where(r => r.EndsLine).ToList();

        Assert.True(endings[0].Style.Tight, "the break inside the paragraph should be tight");
        Assert.False(endings[1].Style.Tight, "the end of the paragraph should have air under it");
        Assert.False(endings[2].Style.Tight, "the next paragraph should have air under it");
        Assert.All(endings.Skip(3), e => Assert.True(e.Style.Tight, "a list's items should be tight"));
    }

    // ---- Finding the way back to the markdown ---------------------------------------------------

    /// <summary>Where the markdown behind the rendered word <paramref name="needle"/> starts.</summary>
    private static int SourceOf(MarkdownRendering rendering, string needle)
    {
        var at = rendering.Text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{needle}' is not in the rendered text: '{Shown(rendering)}'");
        return rendering.SourceAt(at);
    }

    /// <summary>Asserts that a word in the rendering maps back to where it was written.</summary>
    private static void MapsBack(string markdown, string needle)
    {
        var rendering = Render(markdown);
        var written = markdown.IndexOf(needle, StringComparison.Ordinal);
        var mapped = SourceOf(rendering, needle);

        Assert.True(written == mapped, $"'{needle}' was written at {written} and maps to {mapped}. Rendered: '{Shown(rendering)}'");
    }

    [Fact]
    public void Markdown_shown_as_written_still_knows_where_a_click_lands()
    {
        // Nested past what the parser will take, so it goes up as the account wrote it. It's the
        // markdown on screen, so a character is where it says it is.
        var markdown = new string('>', 200) + " still here";
        var rendering = Render(markdown);
        var at = markdown.IndexOf("still", StringComparison.Ordinal);

        Assert.Equal(markdown, rendering.Text.TrimEnd('\n'));
        Assert.Equal(at, rendering.SourceAt(at));
    }

    [Fact]
    public void Markdown_shown_as_written_maps_past_its_line_endings()
    {
        // A return and newline is one character once drawn, so each one above a click is a character
        // the rendering has and the markdown has two of.
        var markdown = new string('>', 200) + " first\r\nsecond\r\nthird line";
        var rendering = Render(markdown);

        Assert.Equal(markdown.IndexOf("third", StringComparison.Ordinal), SourceOf(rendering, "third"));
        Assert.Equal(markdown.IndexOf("second", StringComparison.Ordinal), SourceOf(rendering, "second"));
    }

    [Fact]
    public void A_word_in_the_rendering_knows_where_it_was_written()
    {
        // What puts the caret where the user was pointing when they ask to type.
        const string markdown = "Some **bold** text";

        MapsBack(markdown, "bold");
        MapsBack(markdown, "Some");
        MapsBack(markdown, "text");
    }

    [Fact]
    public void An_offset_inside_a_word_maps_through_it_rather_than_to_its_start()
    {
        var rendering = Render("abcdefgh");

        Assert.Equal(0, rendering.SourceAt(0));
        Assert.Equal(3, rendering.SourceAt(3));
        Assert.Equal(7, rendering.SourceAt(7));
    }

    [Fact]
    public void A_line_below_the_first_maps_past_the_lines_above_it()
        => MapsBack("# A heading\n\nThe *body* of it", "body");

    [Fact]
    public void A_word_after_a_broken_line_still_knows_where_it_was_written()
    {
        // Every offset after a line ending rides on it being counted as one character.
        const string markdown = "First - the one\nSecond - the other\nand a third line";

        MapsBack(markdown, "Second");
        MapsBack(markdown, "third");
    }

    [Fact]
    public void A_word_below_a_fenced_block_still_knows_where_it_was_written()
    {
        // A fenced block arrives as a single run carrying its own line endings. Three lines rather
        // than two, because the ending that closes the run absorbs one of the ones inside it — so a
        // block of two hides a miscount that a block of three shows. Asked of the word starting the
        // run below and of one inside it, because the two fail to different faults.
        const string markdown = "before\n\n```\nfirst line\nsecond line\nthird line\n```\n\nafter the block";

        MapsBack(markdown, "after");
        MapsBack(markdown, "block");
    }

    [Fact]
    public void The_words_of_a_link_map_to_the_words_and_not_to_the_address()
        => MapsBack("See [the docs](https://example.com/path) for more", "the docs");

    [Fact]
    public void Code_maps_to_the_code_and_not_to_the_backtick_in_front_of_it()
        => MapsBack("Run `dotnet build` first", "dotnet");

    [Fact]
    public void An_angle_bracketed_url_maps_to_the_url_and_not_to_the_bracket()
        => MapsBack("See <https://example.com/x> for more", "https");

    [Fact]
    public void A_bullet_maps_to_the_item_it_marks_rather_than_to_the_line_before_it()
    {
        // The marker is drawn rather than written, so it belongs to no run of the markdown. Landing
        // on the text it introduces is what a click on it was aiming at.
        const string markdown = "before\n\n- the item";
        var rendering = Render(markdown);
        var bullet = rendering.Text.IndexOf('•');

        Assert.True(bullet >= 0, $"no bullet in: '{Shown(rendering)}'");
        Assert.Equal(markdown.IndexOf("the item", StringComparison.Ordinal), rendering.SourceAt(bullet));
    }

    [Fact]
    public void An_offset_past_everything_lands_at_the_end_of_the_markdown()
    {
        // Clicking in the empty space below a short description. The end is where a caret goes when
        // there is nothing under the pointer, since that is where more of it would be written.
        const string markdown = "a short description";

        Assert.Equal(markdown.Length, Render(markdown).SourceAt(500));
    }

    [Fact]
    public void Nothing_at_all_maps_to_the_start()
    {
        var rendering = Render(string.Empty);

        Assert.Equal(0, rendering.SourceAt(0));
        Assert.Equal(0, rendering.SourceAt(40));
    }

    [Theory]
    [InlineData("plain words")]
    [InlineData("a\nb\nc")]
    [InlineData("```\nfirst line\nsecond line\nthird line\n```")]
    [InlineData("# H\n\n> quoted\n\n- [ ] a box\n\n[a link](https://example.com) after")]
    [InlineData("A soft line break\vmid-description, and one at the end\v")]
    public void The_text_is_as_long_as_the_rendering_says(string markdown)
    {
        // Every offset into the rendering is an offset into this text, which is what the tests above
        // look words up in.
        var rendering = Render(markdown);

        Assert.Equal(rendering.Length, rendering.Text.Length);
    }
}
