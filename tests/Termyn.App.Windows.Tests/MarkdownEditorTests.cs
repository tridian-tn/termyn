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
    private const int VkShift = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [WinFormsFact]
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

    [WinFormsFact]
    public void Refilling_keeps_a_selection_and_not_only_a_caret()
    {
        using var editor = Editing("The quick brown fox jumps over the lazy dog");
        editor.Select(4, 5); // "quick"

        editor.Refill("The quick brown fox leaps over the lazy dog");

        Assert.Equal(4, editor.SelectionStart);
        Assert.Equal(5, editor.SelectionLength);
    }

    [WinFormsFact]
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

    /// <param name="host">
    /// A window to put it in, for a test that presses keys at it. Left on its own the control has no
    /// form above it, and a key held with Alt never reaches it the way it would in the app
    /// </param>
    private static MarkdownEditor Editing(string markdown, Form? host = null)
    {
        var editor = new MarkdownEditor { Theme = Theme.Resolve(ThemePreference.Light) };
        if (host is not null)
        {
            host.Controls.Add(editor);
            host.CreateControl();
        }

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

        Pick(editor, at, needle.Length);
        return editor.SelectionFont!;
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
    private static void Pick(MarkdownEditor editor, int at, int length)
    {
        editor.Select(at, length);

        Assert.True(
            editor.SelectionStart == at && editor.SelectionLength == length,
            $"asked for {length} characters at {at} and got {editor.SelectionLength} at "
            + $"{editor.SelectionStart}. Nothing read from this selection would be about the right "
            + $"place. In the box: '{Shown(editor.Text)}'");
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


    [WinFormsFact]
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

    [WinFormsTheory]
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

    [WinFormsFact]
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

    [WinFormsTheory]
    [InlineData(Keys.Shift | Keys.Return)]
    [InlineData(Keys.Control | Keys.Shift | Keys.Return)]
    public void Shift_and_Return_at_the_end_of_a_description_leaves_a_line_as_well(Keys keys)
    {
        // A rich edit control takes Return with Shift held as a soft line break, U+000B, and the
        // styling drops one at the end of the document — so this was the fault above again, reached
        // through the Shift+Enter a chat app teaches people to press for a new line.
        using var host = new Form();
        using var editor = Editing("Notes", host);
        editor.Select(editor.TextLength, 0);

        Press(editor, keys);
        Assert.Equal("Notes\n", editor.Text);

        editor.Restyle();

        Assert.Equal("Notes\n", editor.Text);
        Assert.Equal(6, editor.SelectionStart);
    }

    [WinFormsTheory]
    [InlineData(4, 0)]
    [InlineData(5, 4)]
    public void Shift_and_Return_does_what_Return_does(int at, int length)
    {
        // In the middle of a description the soft break survived the styling, and was saved to the
        // account as a U+000B, which markdown doesn't read as a line break. So what the box holds
        // after it has to be what it holds after Return: from a caret, and over a selection it
        // replaces.
        using var plainHost = new Form();
        using var plain = Editing("Some bold words", plainHost);
        Pick(plain, at, length);
        Press(plain, Keys.Return);

        using var host = new Form();
        using var editor = Editing("Some bold words", host);
        Pick(editor, at, length);
        Press(editor, Keys.Shift | Keys.Return);

        Assert.Equal(plain.Text, editor.Text);
        Assert.Equal(plain.SelectionStart, editor.SelectionStart);
    }

    [WinFormsFact]
    public void Shift_and_Return_is_an_edit_like_any_other()
    {
        // The window saves the description and notes it for undo when the box says its text has
        // changed. A line put in that the box kept quiet about would be on screen and nowhere else.
        using var host = new Form();
        using var editor = Editing("Notes", host);
        editor.Select(editor.TextLength, 0);

        var changes = 0;
        editor.TextChanged += (_, _) => changes++;

        Press(editor, Keys.Shift | Keys.Return);

        Assert.True(changes > 0, "the box didn't say its text had changed");

        // And the undo it goes into is the window's, not the control's.
        Assert.False(editor.CanUndo);
    }

    [WinFormsFact]
    public void Shift_and_Return_changes_nothing_in_a_box_that_cannot_be_written_in()
    {
        // Read-only is how the box sits with no task under it, or one the account won't let this
        // user change. The control ignores the key there, and putting the line in by hand mustn't
        // get round that.
        using var host = new Form();
        using var editor = Editing("Notes", host);
        editor.ReadOnly = true;
        editor.Select(editor.TextLength, 0);

        Press(editor, Keys.Shift | Keys.Return);

        Assert.Equal("Notes", editor.Text);
    }


    [WinFormsFact]
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

    [WinFormsFact]
    public void The_markers_stay_on_screen()
    {
        // A box whose text rearranges itself as you type is worse than one that doesn't. The
        // asterisks are part of what is being written and they stay where they were written.
        using var editor = Editing("Some **bold** here");

        Assert.Contains("**", editor.Text);
    }

    [WinFormsTheory]
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

    [WinFormsFact]
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

    [WinFormsFact]
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

    [WinFormsFact]
    public void Each_heading_level_is_smaller_than_the_one_above_it()
    {
        using var editor = Editing("# one\n\n## two\n\n### three\n\nbody");

        // Read once each and said in full, for the same reason the rendered view's assertions were
        // given that treatment: this fails on a build agent and passes on a re-run of the same
        // commit, and "Assert.True() Failure" is three lines to choose between and nothing at all
        // about any of them.
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

    [WinFormsFact]
    public void Bold_is_bold_and_italic_is_italic_and_struck_is_struck()
    {
        using var editor = Editing("Some **bold** and *italic* and ~~struck~~ here");

        // Read once each, before anything is asserted, and every assertion says what it saw. This
        // one has failed on a build agent and passed on a re-run of the same commit, reporting
        // nothing but "Assert.True() Failure" — which is four lines to choose between and no way to
        // tell styling that didn't apply from text that came out wrong.
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

    [WinFormsFact]
    public void A_links_words_are_coloured_and_its_address_is_not()
    {
        var theme = Theme.Resolve(ThemePreference.Light);
        using var editor = Editing("See [the docs](https://example.com/path) now");

        Assert.Equal(theme.Accent, ColourAt(editor, "the docs"));
        Assert.Equal(theme.Muted, ColourAt(editor, "https://example.com/path"));
        Assert.NotEqual(theme.Accent, ColourAt(editor, "See"));
    }

    [WinFormsFact]
    public void A_checkbox_is_not_drawn_as_a_link()
    {
        // The shape a description most often takes. Drawn as a link it is a page of things that
        // look clickable and aren't.
        var theme = Theme.Resolve(ThemePreference.Light);
        using var editor = Editing("- [ ] still to do");

        Assert.NotEqual(theme.Accent, ColourAt(editor, "[ ]"));
    }

    [WinFormsFact]
    public void Code_is_set_in_a_fixed_width_face()
    {
        using var editor = Editing("Run `dotnet build` first");

        var text = editor.Text;
        var code = FontAt(editor, "dotnet build");
        var plain = FontAt(editor, "Run");

        // Assert.True rather than Assert.Equal, which has no room for a message: the face coming
        // back as the body face is how a selection that didn't take reads, and the two faces and
        // the text tell that apart from the styling having missed this run alone.
        Assert.True(
            code.FontFamily.Name == FontFamily.GenericMonospace.Name,
            $"code wanted {FontFamily.GenericMonospace.Name}. {Drawn("dotnet build", code, text)}");

        Assert.True(
            plain.FontFamily.Name != FontFamily.GenericMonospace.Name,
            $"body should not be fixed width. {Drawn("Run", plain, text)}");
    }

    [WinFormsFact]
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

    [WinFormsFact]
    public void Changing_the_theme_redraws_what_is_already_in_the_box()
    {
        using var editor = Editing("See [the docs](https://example.com) now");

        editor.Theme = Theme.Resolve(ThemePreference.Dark);

        Assert.Equal(Theme.Resolve(ThemePreference.Dark).Accent, ColourAt(editor, "the docs"));
    }

    // ---- Undo, which is the reason any of this is hand-rolled ----------------------------------

    [WinFormsFact]
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

    // ---- Keys that would only change how it looks ----------------------------------------------

    [WinFormsTheory]
    [InlineData(Keys.Control | Keys.E)]
    [InlineData(Keys.Control | Keys.J)]
    [InlineData(Keys.Control | Keys.L)]
    [InlineData(Keys.Control | Keys.R)]
    [InlineData(Keys.Control | Keys.D1)]
    [InlineData(Keys.Control | Keys.D2)]
    [InlineData(Keys.Control | Keys.D5)]
    [InlineData(Keys.Control | Keys.Oemplus)]
    [InlineData(Keys.Control | Keys.Shift | Keys.Oemplus)]
    public void A_formatting_key_the_control_brings_with_it_changes_nothing(Keys keys)
    {
        // Alignment, line spacing and subscript. None of it is markdown, none of it is saved, and
        // all of it stayed on screen until the next edit.
        //
        // From a centred paragraph as well as a plain one, since a key that sets what the text
        // already has would change nothing either way — Ctrl+L on text already to the left.
        foreach (var centred in new[] { false, true })
        {
            using var host = new Form();
            using var editor = Editing("Some **bold** words\nand a second line", host);
            if (centred)
            {
                editor.SelectAll();
                editor.SelectionAlignment = HorizontalAlignment.Center;
            }

            Pick(editor, 5, 8);
            var before = editor.Rtf;

            Press(editor, keys);

            Assert.Equal("Some **bold** words\nand a second line", editor.Text);
            Assert.Equal(before, editor.Rtf);
        }
    }

    [WinFormsFact]
    public void A_key_that_changes_the_words_still_changes_them()
    {
        // What keeps the test above honest: pressed the same way, a key the control acts on does
        // reach it. Without this, a harness that delivered nothing would pass every one of those.
        using var host = new Form();
        using var editor = Editing("Some bold words", host);
        editor.Select(editor.TextLength, 0);

        Press(editor, Keys.Control | Keys.Back);

        Assert.Equal("Some bold ", editor.Text);
    }

    [WinFormsTheory]
    [InlineData(Keys.Control | Keys.Alt | Keys.E)]
    [InlineData(Keys.Control | Keys.Alt | Keys.J)]
    [InlineData(Keys.Control | Keys.Alt | Keys.L)]
    [InlineData(Keys.Control | Keys.Alt | Keys.R)]
    [InlineData(Keys.Control | Keys.Alt | Keys.D1)]
    [InlineData(Keys.Control | Keys.Alt | Keys.D2)]
    [InlineData(Keys.Control | Keys.Alt | Keys.D5)]
    [InlineData(Keys.Control | Keys.Alt | Keys.Oemplus)]
    public void AltGr_on_a_turned_away_key_still_types_what_it_types(Keys keys)
    {
        // Ctrl+Alt is AltGr, so each of the keys above with Alt added is a character on some layout
        // — é on a UK one, for Ctrl+E's. Turning the formatting away mustn't take that with it. So
        // whatever a plain box makes of the press, on whatever layout this runs under, the
        // description box has to make the same.
        using var plainHost = new Form();
        var plain = new RichTextBox { Text = "Some bold words" };
        plainHost.Controls.Add(plain);
        plainHost.CreateControl();
        plain.CreateControl();
        plain.Select(5, 4);

        using var host = new Form();
        using var editor = Editing("Some bold words", host);
        Pick(editor, 5, 4);

        Press(plain, keys);
        Press(editor, keys);

        Assert.Equal(plain.Text, editor.Text);
    }

    [DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] state);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);

    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    /// <summary>
    /// Presses a key with its modifiers held, through the message loop the way a keyboard does.
    /// </summary>
    /// <remarks>
    /// Posted and pumped rather than sent, so it takes the whole road a real press does: the
    /// window's key filtering, the translation into a character, and the control's own handling.
    /// The modifiers are set in the thread's key state, which is where both this app and the control
    /// read them from, and cleared again afterwards whatever happens.
    ///
    /// A system key only for Alt without Ctrl, which is how Windows sends them. Posted as a system
    /// key, Ctrl+Alt is a press no keyboard makes, and AltGr's character never arrives — so the
    /// test that it still does would pass whether or not anything here had eaten it.
    /// </remarks>
    private static void Press(RichTextBox box, Keys keys)
    {
        var code = keys & Keys.KeyCode;
        var system = keys.HasFlag(Keys.Alt) && !keys.HasFlag(Keys.Control);
        var context = keys.HasFlag(Keys.Alt) ? 1 << 29 : 0;
        var state = new byte[256];

        if (keys.HasFlag(Keys.Control))
            state[0x11] = state[0xA2] = 0x80;
        if (keys.HasFlag(Keys.Shift))
            state[0x10] = state[0xA0] = 0x80;
        if (keys.HasFlag(Keys.Alt))
            state[0x12] = state[0xA4] = 0x80;

        try
        {
            state[(int)code] = 0x80;
            Hold(state);
            PostMessage(box.Handle, system ? WmSysKeyDown : WmKeyDown, (nint)code, 1 | context);
            Application.DoEvents();

            state[(int)code] = 0;
            Hold(state);
            PostMessage(box.Handle, system ? WmSysKeyUp : WmKeyUp, (nint)code, unchecked((int)0xC0000001) | context);
            Application.DoEvents();
        }
        finally
        {
            Hold(new byte[256]);
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    /// <summary>
    /// Sets which keys this thread takes to be held, whatever the machine's keyboard is doing.
    /// </summary>
    /// <remarks>
    /// A thread's key state follows the real keyboard. A new one starts as a copy of it, and a key
    /// pressed or let go anywhere on the machine reaches the thread the next time it asks about a
    /// key — key by key, over the top of anything set in between. So one key is asked about first,
    /// which takes any change still on its way, and only then is the state set. Set without that, a
    /// Shift pressed in another window while these ran got through: Return came out as a soft line
    /// break, and AltGr and E as É in one box and é in the other.
    ///
    /// Asked about a key rather than read whole, because reading the whole table leaves the change
    /// waiting, and it lands on the next key the control asks about.
    /// </remarks>
    /// <param name="state">Which keys are down, a byte per virtual key as the thread holds them</param>
    private static void Hold(byte[] state)
    {
        GetKeyState(VkShift);
        SetKeyboardState(state);
    }

    // ---- Not losing the user's place -----------------------------------------------------------

    [WinFormsFact]
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

    [WinFormsFact]
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

    [WinFormsFact]
    public void Markdown_nested_past_what_the_parser_will_take_is_still_shown()
    {
        // A description arrives by sync, so this is reachable without anyone having typed it here.
        using var editor = Editing(new string('>', 200) + " still here");

        Assert.Contains("still here", editor.Text);
    }

    [WinFormsFact]
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
