using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

/// <summary>
/// What the highlighter says each part of some markdown is. No control anywhere: the whole point of
/// it living here is that the answer is the same whatever draws it, so it can be asked in the plain.
/// </summary>
public class MarkdownHighlightTests
{
    /// <summary>The style covering the first character of <paramref name="needle"/>.</summary>
    private static MarkdownStyle StyleOf(string markdown, string needle)
    {
        var at = markdown.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{needle}' is not in the markdown");
        return StyleAt(markdown, at);
    }

    private static MarkdownStyle StyleAt(string markdown, int index)
    {
        foreach (var run in MarkdownHighlight.Runs(markdown))
            if (index >= run.Start && index < run.Start + run.Length)
                return run.Style;

        Assert.Fail($"nothing covers offset {index} of '{markdown}'");
        return MarkdownStyle.Text;
    }

    // ---- The shape of the answer ---------------------------------------------------------------

    [Fact]
    public void The_runs_tile_the_source_exactly()
    {
        // What lets a caller walk them straight through. Scintilla in particular styles by
        // advancing a cursor, so a gap or an overlap doesn't misdraw one run — it shifts every
        // run after it.
        const string markdown = "# A heading\n\nSome **bold**, `code`, [a link](https://example.com)\n\n- [ ] a box\n\n> quoted\n\n---";

        var runs = MarkdownHighlight.Runs(markdown);
        var at = 0;

        foreach (var run in runs)
        {
            Assert.Equal(at, run.Start);
            Assert.True(run.Length > 0, $"empty run at {run.Start}");
            at += run.Length;
        }

        Assert.Equal(markdown.Length, at);
    }

    [Fact]
    public void Neighbouring_runs_are_never_the_same_style()
    {
        // Joined up rather than emitted per character, so a caller isn't handed thousands of runs
        // for a description that is mostly plain text.
        var runs = MarkdownHighlight.Runs("Some plain text with **one** bold word in it");

        for (var i = 1; i < runs.Count; i++)
            Assert.NotEqual(runs[i - 1].Style, runs[i].Style);
    }

    [Fact]
    public void Nothing_at_all_gives_nothing_at_all()
    {
        Assert.Empty(MarkdownHighlight.Runs(string.Empty));
        Assert.Empty(MarkdownHighlight.Runs(null));
    }

    [Fact]
    public void Plain_text_is_one_run_of_body()
    {
        var runs = MarkdownHighlight.Runs("Just a sentence, with a comma and a full stop.");

        Assert.Single(runs);
        Assert.Equal(MarkdownStyle.Text, runs[0].Style);
    }

    // ---- What it recognises --------------------------------------------------------------------

    [Fact]
    public void A_headings_words_are_the_heading_and_its_hash_is_a_marker()
    {
        // The words carry the heading and the hash is punctuation, which is what lets a heading be
        // drawn larger than the text around it while the marker stays visible to edit.
        const string markdown = "# A heading\n\nbody";

        Assert.Equal(MarkdownStyle.Marker, StyleAt(markdown, 0));
        Assert.Equal(MarkdownStyle.Heading1, StyleOf(markdown, "A heading"));
        Assert.Equal(MarkdownStyle.Text, StyleOf(markdown, "body"));
    }

    [Fact]
    public void Each_heading_level_is_its_own()
    {
        Assert.Equal(MarkdownStyle.Heading1, StyleOf("# one", "one"));
        Assert.Equal(MarkdownStyle.Heading2, StyleOf("## two", "two"));
        Assert.Equal(MarkdownStyle.Heading3, StyleOf("### three", "three"));
        Assert.Equal(MarkdownStyle.Heading6, StyleOf("###### six", "six"));
    }

    [Fact]
    public void Bold_italic_and_struck_carry_their_words_and_quieten_their_markers()
    {
        const string markdown = "Some **bold** and *italic* and ~~struck~~ text";

        Assert.Equal(MarkdownStyle.Strong, StyleOf(markdown, "bold"));
        Assert.Equal(MarkdownStyle.Emphasis, StyleOf(markdown, "italic"));
        Assert.Equal(MarkdownStyle.Struck, StyleOf(markdown, "struck"));

        // The markers stay on screen — this is the source — but they read as punctuation rather
        // than as part of the sentence.
        Assert.Equal(MarkdownStyle.Marker, StyleOf(markdown, "**"));
        Assert.Equal(MarkdownStyle.Marker, StyleOf(markdown, "~~"));
        Assert.Equal(MarkdownStyle.Text, StyleOf(markdown, "Some"));
    }

    [Fact]
    public void Bold_inside_a_heading_is_still_bold()
    {
        // What the character-by-character build is for: the heading is painted first and the
        // emphasis inside it overwrites, rather than the two overlapping and the caller choosing.
        const string markdown = "# A **strong** heading";

        Assert.Equal(MarkdownStyle.Heading1, StyleOf(markdown, "A "));
        Assert.Equal(MarkdownStyle.Strong, StyleOf(markdown, "strong"));
        Assert.Equal(MarkdownStyle.Heading1, StyleOf(markdown, "heading"));
    }

    [Fact]
    public void Code_is_code_inline_and_fenced()
    {
        Assert.Equal(MarkdownStyle.Code, StyleOf("Run `dotnet build` first", "dotnet build"));
        Assert.Equal(MarkdownStyle.Marker, StyleOf("Run `dotnet build` first", "`"));
        Assert.Equal(MarkdownStyle.Code, StyleOf("```\na fenced block\n```", "a fenced block"));
    }

    [Fact]
    public void A_list_marker_is_quiet_and_its_words_are_not()
    {
        const string bullets = "- a bullet\n- another";

        Assert.Equal(MarkdownStyle.Marker, StyleAt(bullets, 0));
        Assert.Equal(MarkdownStyle.Text, StyleOf(bullets, "a bullet"));

        const string numbers = "1. first\n2. second";

        Assert.Equal(MarkdownStyle.Marker, StyleAt(numbers, 0));
        Assert.Equal(MarkdownStyle.Text, StyleOf(numbers, "first"));
    }

    [Fact]
    public void A_list_on_the_very_first_line_is_still_a_list()
    {
        // Lexilla needs a line before it to see either of these — headings it manages, lists and
        // quotations it doesn't. A description that opens with a list is not an edge case.
        Assert.Equal(MarkdownStyle.Marker, StyleAt("- opening with a bullet", 0));
        Assert.Equal(MarkdownStyle.Marker, StyleAt("> opening with a quote", 0));
    }

    [Fact]
    public void A_quotation_is_quoted_and_its_marker_is_a_marker()
    {
        const string markdown = "before\n\n> quoted words\n\nafter";

        Assert.Equal(MarkdownStyle.Marker, StyleOf(markdown, ">"));
        Assert.Equal(MarkdownStyle.Quote, StyleOf(markdown, "quoted words"));
        Assert.Equal(MarkdownStyle.Text, StyleOf(markdown, "after"));
    }

    [Fact]
    public void A_rule_is_a_rule()
        => Assert.Equal(MarkdownStyle.Rule, StyleOf("above\n\n---\n\nbelow", "---"));

    // ---- Links, which are where the judgement is -----------------------------------------------

    [Fact]
    public void A_links_words_are_coloured_and_its_address_is_not()
    {
        const string markdown = "See [the docs](https://example.com/path) for more";

        Assert.Equal(MarkdownStyle.LinkText, StyleOf(markdown, "the docs"));
        Assert.Equal(MarkdownStyle.Url, StyleOf(markdown, "https://example.com/path"));
        Assert.Equal(MarkdownStyle.Marker, StyleOf(markdown, "["));
        Assert.Equal(MarkdownStyle.Marker, StyleOf(markdown, "]"));
        Assert.Equal(MarkdownStyle.Text, StyleOf(markdown, "See"));
    }

    [Fact]
    public void A_links_words_may_have_brackets_of_their_own()
    {
        // They are allowed to, so long as they balance, and the closing one used to be found by
        // taking the first bracket after the link began — which is the inner one here. That stopped
        // a character short: the last of the words was drawn as syntax and the opening parenthesis
        // was drawn as part of the address.
        const string markdown = "See [foo [bar]](https://example.com/path) now";

        Assert.Equal(MarkdownStyle.LinkText, StyleOf(markdown, "foo [bar]"));
        Assert.Equal(MarkdownStyle.LinkText, StyleAt(markdown, markdown.IndexOf("]](", StringComparison.Ordinal)));
        Assert.Equal(MarkdownStyle.Url, StyleOf(markdown, "https://example.com/path"));

        // The bracket that closes the words and the parenthesis that opens the address, either side
        // of each other and both of them syntax.
        var closing = markdown.IndexOf("](", StringComparison.Ordinal);
        Assert.Equal(MarkdownStyle.Marker, StyleAt(markdown, closing));
        Assert.Equal(MarkdownStyle.Marker, StyleAt(markdown, closing + 1));
    }

    [Fact]
    public void An_escaped_bracket_in_a_links_words_is_not_counted_as_one()
    {
        // Asked of the words rather than of the address: a link whose words are cut short early
        // still has the address inside what is then drawn as the address, so only the words show
        // the difference.
        const string markdown = @"See [a \] bracket](https://example.com/path) now";

        Assert.Equal(MarkdownStyle.LinkText, StyleOf(markdown, "bracket"));
        Assert.Equal(MarkdownStyle.Url, StyleOf(markdown, "https://example.com/path"));
    }

    [Theory]
    [InlineData("See [a `]` code](https://example.com/path) now", "code")]
    [InlineData("See [a ``] `` two](https://example.com/path) now", "two")]
    [InlineData("See [a `` `]` `` tick](https://example.com/path) now", "tick")]
    public void A_bracket_between_backticks_closes_nothing(string markdown, string after)
    {
        // Code is read before any of this, so a bracket between backticks is a character of the
        // code and closes nothing. A run of backticks is closed by a run of exactly as many, which
        // is what lets the third of these hold a backtick of its own.
        //
        // Asked of the word after the code, which is still part of the link's words: a label cut
        // short at the bracket leaves everything from there to the address drawn as the address.
        Assert.Equal(MarkdownStyle.LinkText, StyleOf(markdown, after));
        Assert.Equal(MarkdownStyle.Url, StyleOf(markdown, "https://example.com/path"));
        Assert.Equal(MarkdownStyle.Marker, StyleAt(markdown, markdown.LastIndexOf("](", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_bare_url_is_a_link()
    {
        // Lexilla misses both of these outright, and people paste them far more often than they
        // write a proper link.
        Assert.Equal(MarkdownStyle.LinkText, StyleOf("See https://example.com for more", "https://example.com"));
        Assert.Equal(MarkdownStyle.LinkText, StyleOf("See <https://example.com/x> now", "https://example.com/x"));
    }

    [Fact]
    public void A_checkbox_is_a_marker_and_not_a_link()
    {
        // Lexilla reads "[ ]" as the opening of a link, so every box in a checklist is drawn as
        // something to click that then does nothing. This is the commonest shape a description
        // takes, so getting it wrong is not a corner.
        const string markdown = "- [ ] still to do\n- [x] done";

        Assert.Equal(MarkdownStyle.Marker, StyleOf(markdown, "[ ]"));
        Assert.Equal(MarkdownStyle.Marker, StyleOf(markdown, "[x]"));
        Assert.Equal(MarkdownStyle.Text, StyleOf(markdown, "still to do"));
    }

    [Fact]
    public void A_link_that_is_not_a_web_address_is_not_coloured_as_one()
    {
        // The same judgement the rendered view makes. Drawn in the link colour it invites a click
        // that then does nothing at all — and a description arrives from an account and gets
        // pasted into from anywhere.
        const string markdown = "[a file](file:///C:/Windows/System32/cmd.exe)";

        Assert.Equal(MarkdownStyle.Text, StyleOf(markdown, "a file"));

        // The address is still shown as an address, because it is still what is written there.
        Assert.Equal(MarkdownStyle.Url, StyleOf(markdown, "file:///"));
    }

    // ---- Not falling over ----------------------------------------------------------------------

    [Fact]
    public void Markdown_nested_past_what_the_parser_will_take_is_left_plain()
    {
        // The parser refuses this by throwing, and a description arrives by sync — so a description
        // written on another device must not be able to take the window down. Unhighlighted is a
        // fine answer.
        var runs = MarkdownHighlight.Runs(new string('>', 200) + " still here");

        Assert.Single(runs);
        Assert.Equal(MarkdownStyle.Text, runs[0].Style);
    }

    [Fact]
    public void A_list_nested_past_what_the_parser_will_take_is_left_plain()
    {
        var deep = string.Concat(Enumerable.Range(0, 80).Select(i => new string(' ', i * 2) + "- level\n"));

        var runs = MarkdownHighlight.Runs(deep);

        Assert.Single(runs);
        Assert.Equal(MarkdownStyle.Text, runs[0].Style);
    }

    [Theory]
    [InlineData("**")]
    [InlineData("[")]
    [InlineData("[](")]
    [InlineData("`")]
    [InlineData("> ")]
    [InlineData("#")]
    [InlineData("- ")]
    [InlineData("~~~")]
    [InlineData("![](")]
    public void Half_written_markdown_still_tiles(string markdown)
    {
        // Every one of these is a description mid-keystroke. The runs still have to cover the text
        // exactly, because that is what the caller walks.
        var at = 0;
        foreach (var run in MarkdownHighlight.Runs(markdown))
        {
            Assert.Equal(at, run.Start);
            at += run.Length;
        }

        Assert.Equal(markdown.Length, at);
    }

    [Fact]
    public void A_description_at_its_full_length_is_still_answered()
    {
        // Sixteen thousand characters is the account's limit, and this runs on every pause in the
        // typing, so it wants to not be slow. Mostly it wants to not be quadratic.
        var big = string.Concat(Enumerable.Repeat("Some **bold** and a [link](https://example.com) here.\n\n", 300));

        var runs = MarkdownHighlight.Runs(big[..16383]);

        Assert.NotEmpty(runs);
        Assert.Equal(16383, runs[^1].Start + runs[^1].Length);
    }
}
