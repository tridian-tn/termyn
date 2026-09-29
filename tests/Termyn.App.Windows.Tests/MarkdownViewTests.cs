using System.Diagnostics;
using System.Runtime.InteropServices;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The rendered half of the description panel. Handing a control a document needs a window behind
/// it, and so does reading one back out of a selection — so each of these realises the control
/// without ever showing it.
/// </summary>
/// <remarks>
/// What each run says, how it's styled, where its links go and where a click maps back to is
/// Presentation's, and tested without a control in <c>MarkdownRenderingTests</c>. What's here is the
/// control's half: that the box draws each run the way its style says, holds exactly as much text as
/// the rendering counts, and answers from the rendering it's showing now rather than the last one.
/// </remarks>
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

        Pick(view, at, needle.Length);

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

    /// <summary>
    /// Selects a stretch, and makes sure the selection went there.
    /// </summary>
    /// <remarks>
    /// A selection that didn't take is left at nought, and everything asked after it then answers
    /// about character nought — so a monospace face comes back as the body face, and the test reads
    /// that as the styling being wrong rather than as its own question having gone astray. It's been
    /// seen when these tests ran alongside others on another thread, which this assembly no longer
    /// does, and said plainly if it's ever seen again.
    /// </remarks>
    private static void Pick(MarkdownView view, int at, int length)
    {
        view.SelectionStart = at;
        view.SelectionLength = length;

        Assert.True(
            view.SelectionStart == at && view.SelectionLength == length,
            $"asked for {length} characters at {at} and got {view.SelectionLength} at {view.SelectionStart}. "
            + $"Nothing read from this selection would be about the right place. "
            + $"Rendered: '{view.Text.ReplaceLineEndings("\\n")}'");
    }

    // ---- What it reads as ----------------------------------------------------------------------

    [WinFormsFact]
    public void Bold_is_bold_and_italic_is_italic()
    {
        // Every assertion here says what it saw. This is the one that failed on a build agent and
        // then passed on a re-run of the same commit, reporting nothing but "Assert.True() Failure"
        // — which narrowed it to one of two lines and told us nothing about either.
        using var view = Render("Some **bold** and some *italic* here");

        // Read once each, before anything is asserted. A message argument is built whether or not
        // the assertion fails, so asking the control again inside it would double the selections
        // this test makes, and would let the message describe a different look at the control from
        // the one that failed.
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

    /// <summary>
    /// How a word actually came out, for an assertion that is about to say it is wrong.
    /// </summary>
    /// <remarks>
    /// With the rendered text alongside it, which separates a rendering that came out wrong from one
    /// that came out right and was styled wrongly.
    /// </remarks>
    private static string Drawn(string needle, Font font, string text)
        => $"'{needle}' is {font.FontFamily.Name} {font.Size}pt {font.Style}. Rendered text: '{text}'";

    [WinFormsFact]
    public void Strikethrough_is_struck_through()
    {
        // Todoist's own editor writes this one with two tildes, which plain markdown has no syntax
        // for at all — so the parser has to be told to read it.
        using var view = Render("This is ~~gone~~ now");

        Assert.True(FontAt(view, "gone").Strikeout);
        Assert.Equal("This is gone now", view.Text.Trim());
    }

    [WinFormsFact]
    public void A_heading_is_larger_and_bold()
    {
        using var view = Render("# A heading\n\nSome text");

        var heading = FontAt(view, "A heading");
        var body = FontAt(view, "Some text");

        Assert.True(heading.Bold);
        Assert.True(heading.Size > body.Size);
        Assert.Equal("A heading", view.Text.Split('\n')[0].Trim());
    }

    [WinFormsFact]
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

    [WinFormsFact]
    public void A_bullets_words_hang_in_from_the_bullet_rather_than_the_other_way_round()
    {
        // The bullet starts the line at the margin and everything after it, wrapped lines included,
        // sits in from it. The other way round reads as a line indented for no reason with a bullet
        // somewhere in front of it.
        using var view = Render("- first\n- second");

        FontAt(view, "first");

        Assert.Equal(0, view.SelectionIndent);
        Assert.True(view.SelectionHangingIndent > 0, $"hangs by {view.SelectionHangingIndent}");
    }

    [WinFormsFact]
    public void A_link_is_coloured_apart_from_the_words_around_it()
    {
        var theme = Theme.Resolve(ThemePreference.Light);
        using var view = Render("See [the docs](https://example.com) for more");

        Assert.Equal(theme.Accent, ColourAt(view, "the docs"));
        Assert.NotEqual(theme.Accent, ColourAt(view, "See"));
    }

    [WinFormsFact]
    public void The_links_from_the_last_task_do_not_linger()
    {
        using var view = Render("[first task](https://one.example)");

        view.Markdown = "The second task, with nothing to click";

        Assert.Null(view.LinkAt(0));
    }

    [WinFormsFact]
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

    [WinFormsFact]
    public void Code_is_set_in_a_fixed_width_face()
    {
        using var view = Render("Run `dotnet build` first");

        var text = view.Text.Trim();
        var code = FontAt(view, "dotnet build");

        // Assert.True rather than Assert.Equal, which has no room for a message: the face coming
        // back as the body face is how a selection that didn't take reads, and the text alongside
        // it says whether the rendering went wrong as well or only the styling did.
        Assert.True(
            code.FontFamily.Name == FontFamily.GenericMonospace.Name,
            $"code wanted {FontFamily.GenericMonospace.Name}. {Drawn("dotnet build", code, text)}");

        Assert.Equal("Run dotnet build first", text);
    }

    [WinFormsTheory]
    [InlineData("```")]
    [InlineData("```js")]
    [InlineData("~~~")]
    [InlineData("```\n```")]
    [InlineData("Some notes\n\n```")]
    public void A_fence_with_nothing_in_it_is_not_the_end_of_the_description(string markdown)
    {
        // Typed as the start of a code block and left there for a moment, which is all it takes for
        // the rendering to be drawn from it. A fence with no lines under it has no lines at all to
        // read, and it used to throw rather than draw nothing — taking down the window, with the
        // description it was typed into still showing the one before it.
        using var view = Render(markdown);

        Assert.Equal(view.TextLength, view.Counted);
        Assert.DoesNotContain("```", view.Text);
    }

    // ---- Not falling over ----------------------------------------------------------------------

    // ---- What used to vanish, and what used to throw -------------------------------------------

    [WinFormsFact]
    public void A_link_that_is_not_a_web_address_is_not_coloured_as_one_either()
    {
        // It isn't clickable, so it shouldn't look clickable. Drawn in the link colour it invites
        // a click that then does nothing at all.
        var theme = Theme.Resolve(ThemePreference.Light);
        using var view = Render("[a file](file:///C:/Windows/System32/cmd.exe)");

        Assert.Equal("a file", view.Text.Trim());
        Assert.NotEqual(theme.Accent, ColourAt(view, "a file"));
    }

    [WinFormsFact]
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

    [WinFormsFact]
    public void Moving_to_another_task_replaces_what_was_there()
    {
        using var view = Render("The first task's description");

        view.Markdown = "The second task's description";

        Assert.DoesNotContain("first", view.Text);
        Assert.Contains("second", view.Text);
    }

    // ---- The rest of the grammar ---------------------------------------------------------------

    [WinFormsFact]
    public void Each_heading_level_is_smaller_than_the_one_above_it()
    {
        using var view = Render("# one\n\n## two\n\n### three\n\nbody");

        var one = FontAt(view, "one").Size;
        var two = FontAt(view, "two").Size;
        var three = FontAt(view, "three").Size;
        var body = FontAt(view, "body").Size;

        // The rendered text alongside the sizes, because these have failed together with the
        // rendering being wrong — and the sizes on their own don't say which of the two it was.
        var seen = $"Rendered: '{view.Text.ReplaceLineEndings("\\n")}'";

        Assert.True(one > two, $"h1 {one} should beat h2 {two}. {seen}");
        Assert.True(two > three, $"h2 {two} should beat h3 {three}. {seen}");
        Assert.True(three > body, $"h3 {three} should beat body {body}. {seen}");
    }

    [WinFormsFact]
    public void A_nested_list_sits_in_from_the_one_it_belongs_to()
    {
        using var view = Render("- outer\n    - inner");

        FontAt(view, "outer");
        var outer = view.SelectionIndent;
        FontAt(view, "inner");

        Assert.True(view.SelectionIndent > outer, $"inner {view.SelectionIndent} should sit in from outer {outer}");
    }

    [WinFormsFact]
    public void A_quote_sits_in_from_the_text_around_it()
    {
        // Its marker is dropped, so the indent is the only thing that says it is a quotation.
        using var view = Render("before\n\n> quoted\n\nafter");

        FontAt(view, "before");
        var body = view.SelectionIndent;
        FontAt(view, "quoted");

        Assert.True(view.SelectionIndent > body, $"quote {view.SelectionIndent} should sit in from body {body}");
    }

    [WinFormsFact]
    public void Changing_the_theme_redraws_what_is_already_on_screen()
    {
        // The only thing that recolours the panel when the app switches theme.
        using var view = Render("See [the docs](https://example.com) now");

        view.Theme = Theme.Resolve(ThemePreference.Dark);

        Assert.Equal(Theme.Resolve(ThemePreference.Dark).Accent, ColourAt(view, "the docs"));
        Assert.Equal("https://example.com/", view.LinkAt(view.Text.IndexOf("the docs", StringComparison.Ordinal)));
    }

    // ---- Line breaks ----------------------------------------------------------------------------

    [WinFormsFact]
    public void A_paragraph_has_air_under_it_and_a_line_broken_inside_one_does_not()
    {
        // The difference between a new line and a new thought, which is the reason for having both.
        // Nothing else says which is which once the markdown's own blank lines are gone.
        using var view = Render("First line\nSecond line\n\nNext thought\n\n- an item\n- another");

        Assert.Equal(0, SpaceAfter(view, "First line"));
        Assert.True(SpaceAfter(view, "Second line") > 0, "a paragraph's last line should have air under it");
        Assert.True(SpaceAfter(view, "Next thought") > 0, "a paragraph should have air under it");
        Assert.Equal(0, SpaceAfter(view, "an item"));
    }

    /// <summary>How much air is under the paragraph some words are in, in twips.</summary>
    private static int SpaceAfter(MarkdownView view, string needle)
    {
        FontAt(view, needle);

        var format = new ParaFormat2 { cbSize = Marshal.SizeOf<ParaFormat2>(), dwMask = PfmSpaceAfter };
        SendMessage(view.Handle, EmGetParaFormat, 0, ref format);
        return format.dySpaceAfter;
    }

    private const int EmGetParaFormat = 0x0400 + 61;
    private const int PfmSpaceAfter = 0x00000080;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, int flags, ref ParaFormat2 format);

    /// <summary>The rich edit control's paragraph format, every field present so its size is right.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ParaFormat2
    {
        public int cbSize;
        public int dwMask;
        public short wNumbering;
        public short wEffects;
        public int dxStartIndent;
        public int dxRightIndent;
        public int dxOffset;
        public short wAlignment;
        public short cTabCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public int[] rgxTabs;

        public int dySpaceBefore;
        public int dySpaceAfter;
        public int dyLineSpacing;
        public short sStyle;
        public byte bLineSpacingRule;
        public byte bOutlineLevel;
        public short wShadingWeight;
        public short wShadingStyle;
        public short wNumberingStart;
        public short wNumberingStyle;
        public short wNumberingTab;
        public short wBorderSpace;
        public short wBorderWidth;
        public short wBorders;
    }

    // ---- The box and the rendering agreeing -----------------------------------------------------
    [WinFormsTheory]
    [InlineData("plain words")]
    [InlineData("a\nb\nc")]
    [InlineData("```\nfirst line\nsecond line\nthird line\n```")]
    [InlineData("# H\n\n> quoted\n\n- [ ] a box\n\n[a link](https://example.com) after")]
    [InlineData("An em dash — a résumé, 日本語, an emoji 🎉 and a tab\there")]
    [InlineData("Pasted in the wrong encoding: caf\uFFFD and na\uFFFDve")]
    [InlineData("A soft line break\vmid-description, and one at the end\v")]
    public void The_writing_and_the_box_agree_about_how_much_is_in_it(string markdown)
    {
        // Every offset recorded against a run rides on these two agreeing — where a run came from in
        // the markdown, and where a link starts and stops.
        using var view = Render(markdown);

        Assert.Equal(view.TextLength, view.Counted);
    }

    [WinFormsFact]
    public void A_return_the_parser_never_saw_is_counted_as_the_box_holds_it()
    {
        // Markdown nested past what the parser will take is written through exactly as the account
        // sent it, which is the one way a line ending reaches the writing untidied. A return with a
        // newline after it becomes one character where a return on its own stays one, so counting
        // every return as vanishing leaves the writing a character behind for each of them.
        var markdown = new string('>', 200) + " before\rafter";
        using var view = Render(markdown);

        Assert.Equal(view.TextLength, view.Counted);
    }

    [WinFormsFact]
    public void Half_an_emoji_still_counts_as_one()
    {
        // What's left of a description cut off mid-emoji by whatever wrote it last: one half of a
        // pair, which the box drops from a document. Parsed and unparsed, since the second is written
        // through exactly as it came.
        foreach (var half in new[] { (char)0xD83C, (char)0xDF89 })
        {
            var markdown = $"before {half} after";

            using var parsed = Render(markdown);
            using var unparsed = Render(new string('>', 200) + markdown);

            Assert.Equal(parsed.TextLength, parsed.Counted);
            Assert.Equal(unparsed.TextLength, unparsed.Counted);
        }
    }

    [WinFormsFact]
    public void A_character_the_box_cannot_hold_still_counts_as_one()
    {
        // The replacement character is what text pasted in the wrong encoding is full of, and the box
        // drops it from a document where it shows a space for one typed in; a nought ends the
        // document where it stands. Unparsed, both reach the writing exactly as the account sent
        // them — the parser swaps a nought for the replacement character, but only when it parses.
        var markdown = new string('>', 200) + " caf\uFFFD, a\0b and after";
        using var view = Render(markdown);

        Assert.Equal(view.TextLength, view.Counted);
        Assert.Contains("and after", view.Text);
    }

    [WinFormsFact]
    public void A_description_at_its_full_length_is_drawn_faster_than_a_pause()
    {
        // It's drawn again every time a task is selected and every time a sync lands on the one on
        // screen, next to the box the user may be typing in. Generous by design — this is a
        // regression gate rather than a benchmark, and it runs on whatever the build agent happens
        // to be.
        var big = string.Concat(Enumerable.Repeat("Some **bold** and a [link](https://example.com) here.\n\n", 400))[..16_383];

        // Warmed first, so this measures the drawing rather than the first use of everything under
        // it.
        using var view = Render(big);

        var clock = Stopwatch.StartNew();
        view.Markdown = big + " ";
        clock.Stop();

        // Measured at 7 ms for the full sixteen thousand characters, against 580 ms for the same
        // thing written a run at a time. What this guards against is a return to that shape, which
        // was eighty times slower rather than a few per cent.
        Assert.True(clock.ElapsedMilliseconds < 300, $"drawing a full description took {clock.ElapsedMilliseconds} ms");
    }

    [WinFormsTheory]
    [InlineData("before\n\n```\nfirst line\nsecond line\nthird line\n```\n\nafter the block")]
    [InlineData("# H\n\n> quoted\n\n- [ ] a box\n- **bold** item\n\n[a link](https://example.com) after\n\n---\n\nend")]
    [InlineData("a\nb  \nc\n\n1. one\n2. two")]
    public void The_box_holds_the_rendering_character_for_character(string markdown)
    {
        // Every offset the rendering hands out — where a link is, where a click maps back to — is an
        // offset into its own text, so the box has to hold that text in that order, not just as much
        // of it. A run written after its line ending instead of before would keep the length right
        // and move a fenced block's lines, the rule, everything inside them.
        using var view = Render(markdown);

        Assert.Equal(MarkdownRendering.Of(markdown).Text, view.Text.ReplaceLineEndings("\n"));
    }

    [WinFormsTheory]
    [InlineData("after")]
    [InlineData("block")]
    [InlineData("and")]
    [InlineData("docs")]
    [InlineData("here")]
    public void A_word_in_the_box_maps_back_to_where_it_was_written(string needle)
    {
        // The rendering's own tests look words up in the text it builds itself. This looks them up in
        // what the box actually holds — past a fenced block of several lines, past broken lines, and
        // inside a link — so a document that put the same characters in a different order couldn't
        // pass for one that agrees with the rendering just by being the right length.
        const string markdown = "before\n\n```\nfirst line\nsecond line\nthird line\n```\n\nafter the block\nand [the docs](https://example.com) here";
        using var view = Render(markdown);

        var at = view.Text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{needle}' is not in the box: '{view.Text.ReplaceLineEndings("\\n")}'");

        Assert.Equal(markdown.IndexOf(needle, StringComparison.Ordinal), view.SourceAt(at));
    }

    [WinFormsFact]
    public void A_link_further_down_is_found_where_the_box_has_it()
    {
        // The same agreement for the address a click follows.
        using var view = Render("A first line\n\n```\none\ntwo\n```\n\nthen [the docs](https://example.com) here");

        Assert.Equal("https://example.com/", view.LinkAt(view.Text.IndexOf("the docs", StringComparison.Ordinal)));
        Assert.Null(view.LinkAt(view.Text.IndexOf("here", StringComparison.Ordinal)));
    }

    [WinFormsFact]
    public void The_offsets_from_the_last_task_do_not_linger()
    {
        // Same failure the links had: a map left over from the task before points the caret into a
        // description that is no longer on screen.
        using var view = Render("a much longer first description than the one that follows it");

        view.Markdown = "short";

        Assert.Equal("short".Length, view.SourceAt(500));
    }

    [WinFormsFact]
    public void The_box_is_read_only()
    {
        // The account's text is what gets saved. A rendering that could be edited would have to be
        // serialised back to markdown, and that is where formatting quietly goes missing.
        using var view = Render("Anything");

        Assert.True(view.ReadOnly);
    }

    // ---- Looking like something you can't type into ----------------------------------------------

    [WinFormsFact]
    public void An_inert_pane_is_recessed_onto_the_background()
    {
        // The pane is kept out of use by being read-only rather than disabled — disabling a control
        // that has the focus hands the focus to the outline, where space ticks a task off. So this
        // is the only thing on screen saying it won't take any typing.
        var theme = Theme.Resolve(ThemePreference.Light);
        using var view = Render("some description");

        Assert.Equal(theme.Panel, view.BackColor);

        view.Inert = true;

        Assert.Equal(theme.Background, view.BackColor);
        Assert.NotEqual(theme.Panel, theme.Background);
    }

    [WinFormsFact]
    public void Coming_back_into_use_looks_like_the_pane_it_was()
    {
        var theme = Theme.Resolve(ThemePreference.Light);
        using var view = Render("some description");
        view.Inert = true;

        view.Inert = false;

        Assert.Equal(theme.Panel, view.BackColor);
    }

    [WinFormsFact]
    public void Changing_the_theme_keeps_it_looking_inert()
    {
        // The theme pass runs over every control in the window and would otherwise put the pane
        // back to its ordinary colour while it is still refusing to be typed into.
        using var view = Render("some description");
        view.Inert = true;

        view.Theme = Theme.Resolve(ThemePreference.Dark);

        Assert.Equal(Theme.Resolve(ThemePreference.Dark).Background, view.BackColor);
    }

    [WinFormsFact]
    public void An_inert_pane_is_still_not_disabled()
    {
        // Enabled = false is the thing this must never become: it hands the focus to the next
        // control and never gives it back, and the next control is the outline.
        using var view = Render(string.Empty);

        view.Inert = true;

        Assert.True(view.Enabled);
    }

    [WinFormsFact]
    public void A_line_can_be_shown_over_an_empty_pane()
    {
        using var view = Render(string.Empty);

        view.Placeholder = "Select a task to see its description.";

        Assert.Equal("Select a task to see its description.", view.Placeholder);
        Assert.Equal(string.Empty, view.Text.Trim());
    }

    [WinFormsFact]
    public void An_inert_pane_draws_its_words_muted()
    {
        // The cue that actually carries. The recessed background is two units in the light palette
        // and invisible on screen, so what says "this can't be typed into" is the text itself.
        var theme = Theme.Resolve(ThemePreference.Light);
        using var view = Render("some description here");

        Assert.Equal(theme.Text, ColourAt(view, "some description"));

        view.Inert = true;

        Assert.Equal(theme.Muted, ColourAt(view, "some description"));
    }

    [WinFormsFact]
    public void An_inert_panes_links_keep_their_colour()
    {
        // A completed task's description is still worth following out of, and drawing a link dead while
        // it still works is the mirror of the mistake this is here to avoid.
        var theme = Theme.Resolve(ThemePreference.Light);
        using var view = Render("See [the docs](https://example.com) now");

        view.Inert = true;

        Assert.Equal(theme.Accent, ColourAt(view, "the docs"));
        Assert.Equal("https://example.com/", view.LinkAt(view.Text.IndexOf("the docs", StringComparison.Ordinal)));
    }

    [WinFormsFact]
    public void Coming_back_into_use_puts_the_words_back()
    {
        var theme = Theme.Resolve(ThemePreference.Light);
        using var view = Render("some description here");
        view.Inert = true;

        view.Inert = false;

        Assert.Equal(theme.Text, ColourAt(view, "some description"));
    }
}
