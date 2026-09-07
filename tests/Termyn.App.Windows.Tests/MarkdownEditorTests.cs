using System.Diagnostics;
using System.Runtime.InteropServices;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The box you type a description into. Styling a run means selecting it, and a selection needs a
/// window behind it — so each of these realises the control without ever showing it.
/// </summary>
public class MarkdownEditorTests
{
    private const int WmChar = 0x0102;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int VkReturn = 0x0D;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [Fact]
    public void Refilling_the_box_leaves_the_caret_where_it_was()
    {
        // Assigning Text collapses the caret to nought, and Restyle can't put it back — it saves
        // the selection when it runs, by which time the selection is already nought. So a sync
        // landing on a description being read or written moved the caret to the top of it.
        using var editor = Editing("The quick brown fox jumps over the lazy dog");
        editor.Select(20, 0);

        editor.Refill("The quick brown fox leaps over the lazy dog");

        Assert.Equal(20, editor.SelectionStart);
    }

    [Fact]
    public void Refilling_keeps_a_selection_and_not_only_a_caret()
    {
        using var editor = Editing("The quick brown fox jumps over the lazy dog");
        editor.Select(4, 5); // "quick"

        editor.Refill("The quick brown fox leaps over the lazy dog");

        Assert.Equal(4, editor.SelectionStart);
        Assert.Equal(5, editor.SelectionLength);
    }

    [Fact]
    public void A_place_past_the_end_of_shorter_text_lands_at_the_end_of_it()
    {
        // The place was measured against the text being replaced. A description cut down elsewhere
        // would otherwise be asked for a caret it has no room for.
        using var editor = Editing("The quick brown fox jumps over the lazy dog");
        editor.Select(40, 2);

        editor.Refill("Short");

        Assert.Equal(5, editor.SelectionStart);
        Assert.Equal(0, editor.SelectionLength);
    }

    private static MarkdownEditor Editing(string markdown)
    {
        var editor = new MarkdownEditor { Theme = Theme.Resolve(ThemePreference.Light) };
        editor.CreateControl();
        editor.Text = markdown;
        editor.Restyle();
        return editor;
    }

    /// <summary>The font a stretch of the source is drawn in.</summary>
    private static Font FontAt(MarkdownEditor editor, string needle)
    {
        var at = editor.Text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{needle}' is not in the box: {editor.Text}");

        editor.Select(at, needle.Length);
        return editor.SelectionFont!;
    }

    private static Color ColourAt(MarkdownEditor editor, string needle)
    {
        FontAt(editor, needle);
        return editor.SelectionColor;
    }

    /// <summary>Types into the control the way a keyboard does, rather than by assigning text.</summary>
    private static void Type(MarkdownEditor editor, string text)
    {
        foreach (var c in text)
            SendMessage(editor.Handle, WmChar, c, 0);
    }

    /// <summary>
    /// Presses Return, which a rich edit control turns into a line break on the key rather than on
    /// the character — sending the character alone puts nothing in the box.
    /// </summary>
    private static void PressReturn(MarkdownEditor editor)
    {
        SendMessage(editor.Handle, WmKeyDown, VkReturn, 0);
        SendMessage(editor.Handle, WmChar, VkReturn, 0);
        SendMessage(editor.Handle, WmKeyUp, VkReturn, 0);
    }


    [Fact]
    public void A_task_whose_description_matches_the_last_one_is_still_drawn()
    {
        // Styling is skipped when the text hasn't changed since it was last drawn, which is what
        // keeps the wait for the typing to stop from restyling a box nobody touched. But assigning
        // Text replaces the document with a plain one, so "hasn't changed" and "is still styled"
        // are different questions — and two tasks whose descriptions match to the character used to
        // leave the second one drawn flat.
        using var editor = Editing("# A heading");
        Assert.True(FontAt(editor, "A heading").Bold);

        editor.Text = "# A heading";
        editor.Restyle();

        Assert.True(FontAt(editor, "A heading").Bold);
    }

    // ---- The text is never touched -------------------------------------------------------------

    [Theory]
    [InlineData("Notes\n")]
    [InlineData("Notes\n\n")]
    [InlineData("Notes\n\n\n")]
    [InlineData("\n")]
    [InlineData("# A heading\n")]
    [InlineData("- [ ] a box\n\n")]
    public void A_description_ending_in_new_lines_still_ends_in_all_of_them(string markdown)
    {
        // The last \par of a document ends the paragraph it is on instead of opening an empty one
        // after it, so styling used to give back one newline fewer than it was handed.
        using var editor = Editing(markdown);

        Assert.Equal(markdown, editor.Text);
    }

    [Fact]
    public void Return_at_the_end_of_a_description_leaves_a_line_to_carry_on_typing_on()
    {
        // The fault as it was met: press Return at the end of a description — which is where it is
        // nearly always pressed — and a moment later, when the styling caught up, the line was gone
        // and the caret was back on the end of the one above.
        using var editor = Editing("Notes");
        editor.Select(editor.TextLength, 0);
        PressReturn(editor);
        Assert.Equal("Notes\n", editor.Text);

        editor.Restyle();

        Assert.Equal("Notes\n", editor.Text);
        Assert.Equal(6, editor.SelectionStart);
    }


    [Fact]
    public void Styling_changes_how_the_markdown_looks_and_not_what_it_says()
    {
        // The whole basis of drawing the source rather than a rendering of it: what is on screen is
        // what gets saved, character for character, however it is painted.
        const string markdown = "# A heading\n\nSome **bold**, `code`, [a link](https://example.com)\n\n- [ ] a box";
        using var editor = Editing(markdown);

        Assert.Equal(markdown, editor.Text);

        editor.Restyle();
        editor.Restyle();

        Assert.Equal(markdown, editor.Text);
    }

    [Fact]
    public void The_markers_stay_on_screen()
    {
        // A box whose text rearranges itself as you type is worse than one that doesn't. The
        // asterisks are part of what is being written and they stay where they were written.
        using var editor = Editing("Some **bold** here");

        Assert.Contains("**", editor.Text);
    }

    [Theory]
    [InlineData(@"A brace } in the middle and a { too")]
    [InlineData(@"A backslash \ and a \\ pair")]
    [InlineData(@"Braces around {everything} at once")]
    [InlineData("An em dash — and a résumé and 日本語")]
    [InlineData("An emoji 🎉 in a description")]
    [InlineData("A tab\tbetween words")]
    [InlineData(@"{\rtf1 pretending to be a document}")]
    public void A_description_that_looks_like_the_document_format_survives_being_drawn(string markdown)
    {
        // The styling writes a rich text document and hands it over whole, so a description is
        // account data going into a format with syntax of its own. A brace left alone would end
        // the document early and take the rest of the description with it; anything above ASCII would
        // arrive as mojibake. Both are what a pasted description is full of.
        using var editor = Editing(markdown);

        Assert.Equal(markdown, editor.Text);

        // And again, since what comes back out is what the next restyle reads.
        editor.Text = markdown + " more";
        editor.Restyle();

        Assert.Equal(markdown + " more", editor.Text);
    }

    [Fact]
    public void The_line_endings_the_account_stores_come_back_as_they_went_in()
    {
        // The offsets the rendered view hands over are into this text, and what gets saved is this
        // text. A line ending that grew a carriage return on the way through would move every
        // offset after it and write a different description back to the account on each round trip.
        const string markdown = "first line\nsecond line\n\nafter a gap";
        using var editor = Editing(markdown);

        Assert.Equal(markdown, editor.Text);
    }

    // ---- What it draws -------------------------------------------------------------------------

    [Fact]
    public void A_headings_words_are_larger_and_bold_and_its_hash_is_quiet()
    {
        var theme = Theme.Resolve(ThemePreference.Light);
        using var editor = Editing("# A heading\n\nbody text");

        var heading = FontAt(editor, "A heading");
        var body = FontAt(editor, "body text");

        Assert.True(heading.Bold);
        Assert.True(heading.Size > body.Size, $"heading {heading.Size} should beat body {body.Size}");
        Assert.False(body.Bold);

        Assert.Equal(theme.Muted, ColourAt(editor, "#"));
    }

    [Fact]
    public void Each_heading_level_is_smaller_than_the_one_above_it()
    {
        using var editor = Editing("# one\n\n## two\n\n### three\n\nbody");

        // Read once each and said in full, for the reason #45 gave the rendered view's assertions
        // the same treatment: this fails on a build agent and passes on a re-run of the same commit,
        // and "Assert.True() Failure" is three lines to choose between and nothing about any of
        // them. See #115.
        var text = editor.Text;
        var one = FontAt(editor, "one").Size;
        var two = FontAt(editor, "two").Size;
        var three = FontAt(editor, "three").Size;
        var body = FontAt(editor, "body").Size;

        Assert.True(one > two, $"h1 {one} should beat h2 {two}. In the box: '{Shown(text)}'");
        Assert.True(two > three, $"h2 {two} should beat h3 {three}. In the box: '{Shown(text)}'");
        Assert.True(three > body, $"h3 {three} should beat body {body}. In the box: '{Shown(text)}'");
    }

    /// <summary>Text with its line endings written out, so a message stays on one line.</summary>
    private static string Shown(string text) => text.ReplaceLineEndings("\\n");

    [Fact]
    public void Bold_is_bold_and_italic_is_italic_and_struck_is_struck()
    {
        using var editor = Editing("Some **bold** and *italic* and ~~struck~~ here");

        // Read once each, before anything is asserted, and every assertion says what it saw. This
        // one has failed on a build agent and passed on a re-run of the same commit, reporting
        // nothing but "Assert.True() Failure" — which is four lines to choose between and no way to
        // tell styling that didn't apply from text that came out wrong. See #42.
        var text = editor.Text;
        var bold = FontAt(editor, "bold");
        var italic = FontAt(editor, "italic");
        var struck = FontAt(editor, "struck");
        var plain = FontAt(editor, "Some");

        Assert.True(bold.Bold, Drawn("bold", bold, text));
        Assert.True(italic.Italic, Drawn("italic", italic, text));
        Assert.True(struck.Strikeout, Drawn("struck", struck, text));
        Assert.False(plain.Bold, Drawn("Some", plain, text));
    }

    /// <summary>How a word actually came out, for an assertion that is about to say it is wrong.</summary>
    private static string Drawn(string needle, Font font, string text)
        => $"'{needle}' is {font.FontFamily.Name} {font.Size}pt {font.Style}. Text in the box: '{text}'";

    [Fact]
    public void A_links_words_are_coloured_and_its_address_is_not()
    {
        var theme = Theme.Resolve(ThemePreference.Light);
        using var editor = Editing("See [the docs](https://example.com/path) now");

        Assert.Equal(theme.Accent, ColourAt(editor, "the docs"));
        Assert.Equal(theme.Muted, ColourAt(editor, "https://example.com/path"));
        Assert.NotEqual(theme.Accent, ColourAt(editor, "See"));
    }

    [Fact]
    public void A_checkbox_is_not_drawn_as_a_link()
    {
        // The shape a description most often takes. Drawn as a link it is a page of things that
        // look clickable and aren't.
        var theme = Theme.Resolve(ThemePreference.Light);
        using var editor = Editing("- [ ] still to do");

        Assert.NotEqual(theme.Accent, ColourAt(editor, "[ ]"));
    }

    [Fact]
    public void Code_is_set_in_a_fixed_width_face()
    {
        using var editor = Editing("Run `dotnet build` first");

        var text = editor.Text;
        var code = FontAt(editor, "dotnet build");
        var plain = FontAt(editor, "Run");

        // Assert.True rather than Assert.Equal, which has no room for a message: the face coming
        // back as the body face is how #115 reads, and the two faces and the text tell that apart
        // from the styling having missed this run alone.
        Assert.True(
            code.FontFamily.Name == FontFamily.GenericMonospace.Name,
            $"code wanted {FontFamily.GenericMonospace.Name}. {Drawn("dotnet build", code, text)}");

        Assert.True(
            plain.FontFamily.Name != FontFamily.GenericMonospace.Name,
            $"body should not be fixed width. {Drawn("Run", plain, text)}");
    }

    [Fact]
    public void Taking_the_markers_off_takes_the_boldness_with_them()
    {
        // Styling paints over what was there before rather than adding to it. Without the reset,
        // deleting the asterisks either side of a word leaves the word bold for ever.
        using var editor = Editing("Some **bold** here");
        Assert.True(FontAt(editor, "bold").Bold);

        editor.Text = "Some bold here";
        editor.Restyle();

        Assert.False(FontAt(editor, "bold").Bold);
    }

    [Fact]
    public void Changing_the_theme_redraws_what_is_already_in_the_box()
    {
        using var editor = Editing("See [the docs](https://example.com) now");

        editor.Theme = Theme.Resolve(ThemePreference.Dark);

        Assert.Equal(Theme.Resolve(ThemePreference.Dark).Accent, ColourAt(editor, "the docs"));
    }

    // ---- Undo, which is the reason any of this is hand-rolled ----------------------------------

    [Fact]
    public void The_controls_own_undo_queue_is_switched_off()
    {
        // The measured fact this whole design turns on: a rich edit control records applying a
        // colour or a font as an undoable action, and the Text Object Model's documented way of
        // suspending that does not work. Left on, Ctrl+Z answers by un-highlighting.
        using var editor = Editing(string.Empty);

        Type(editor, "some **bold** words");
        editor.Restyle();

        Assert.False(editor.CanUndo);

        // And it stays off — nothing turns it back on by touching the text.
        editor.Undo();

        Assert.Equal("some **bold** words", editor.Text);
        Assert.True(FontAt(editor, "bold").Bold);
    }

    // ---- Not losing the user's place -----------------------------------------------------------

    [Fact]
    public void The_caret_is_where_it_was_after_a_restyle()
    {
        // It runs on a pause in the typing, which is to say while the user is sitting in the box.
        using var editor = Editing("Some **bold** and more words after it");

        editor.SelectionStart = 20;
        editor.SelectionLength = 0;
        editor.Text += " and more still";
        editor.SelectionStart = 20;

        editor.Restyle();

        Assert.Equal(20, editor.SelectionStart);
        Assert.Equal(0, editor.SelectionLength);
    }

    [Fact]
    public void A_selection_is_still_selected_after_a_restyle()
    {
        using var editor = Editing("Some **bold** and more words after it");

        editor.Select(5, 8);
        editor.Restyle();

        // Forced, since the text hasn't changed and an unchanged one isn't styled again.
        editor.Text += "!";
        editor.Select(5, 8);
        editor.Restyle();

        Assert.Equal(5, editor.SelectionStart);
        Assert.Equal(8, editor.SelectionLength);
    }

    // ---- Not falling over, and not being slow ---------------------------------------------------

    [Fact]
    public void Markdown_nested_past_what_the_parser_will_take_is_still_shown()
    {
        // A description arrives by sync, so this is reachable without anyone having typed it here.
        using var editor = Editing(new string('>', 200) + " still here");

        Assert.Contains("still here", editor.Text);
    }

    [Fact]
    public void A_description_at_its_full_length_is_styled_faster_than_a_pause()
    {
        // It runs 300 ms after the typing stops, in the box the user is working in, so what it
        // must not be is noticeable. Generous by design — this is a regression gate rather than a
        // benchmark, and it runs on whatever the build agent happens to be.
        var big = string.Concat(Enumerable.Repeat("Some **bold** and a [link](https://example.com) here.\n\n", 400))[..16_383];

        using var editor = Editing(string.Empty);
        editor.Text = big;

        // Warmed first, so this measures the styling rather than the first use of everything under
        // it. Unwarmed reads several times slower, which is how the write-visible figure in
        // performance.md was first misread.
        editor.Restyle();
        editor.Text = big + " ";

        var clock = Stopwatch.StartNew();
        editor.Restyle();
        clock.Stop();

        // Measured at 18 ms for the full sixteen thousand characters, against 1,583 ms for the
        // same thing applied a run at a time. The gate is loose because it runs on whatever the
        // build agent happens to be; what it is guarding against is a return to the run-at-a-time
        // shape, which was two orders of magnitude away rather than a few per cent.
        Assert.True(clock.ElapsedMilliseconds < 300, $"styling a full description took {clock.ElapsedMilliseconds} ms");
    }
}
