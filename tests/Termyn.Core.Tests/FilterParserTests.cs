using Termyn.Core.Filters;
using Termyn.Core.Model;

namespace Termyn.Core.Tests;

/// <summary>The normative filter grammar: what it reads, and what it refuses to guess at.</summary>
public class FilterParserTests
{
    /// <summary>
    /// The names an account is pretending to have. "Work Learning" begins with "Work" on purpose:
    /// the parser has to prefer the longer of two names it knows, and without a pair like that
    /// there is nothing for it to prefer.
    /// </summary>
    private static readonly FilterVocabulary Vocabulary =
        new(["Work", "Work Learning", "My Project"], ["home", "deep work"]);

    // ---- Terms -------------------------------------------------------------------------------------

    [Fact]
    public void A_project_term_reads_its_name()
    {
        var parsed = Parse("#Work");

        var project = Assert.IsType<FilterExpression.InProject>(parsed.Expression);
        Assert.Equal("Work", project.Name);
        Assert.False(project.IncludeSubProjects);
    }

    [Fact]
    public void A_double_hash_takes_the_sub_projects_too()
        => Assert.True(Assert.IsType<FilterExpression.InProject>(Parse("##Work").Expression).IncludeSubProjects);

    [Fact]
    public void A_name_with_spaces_is_read_whole()
    {
        // Only the account's own names can say where "#My Project" ends.
        var project = Assert.IsType<FilterExpression.InProject>(Parse("#My Project").Expression);
        Assert.Equal("My Project", project.Name);
    }

    [Fact]
    public void The_longest_name_that_exists_wins_over_a_shorter_one()
    {
        // "Work" is a name too, so stopping at the first one that matches would read this as that
        // project and leave "Learning" as a word of its own.
        var and = Assert.IsType<FilterExpression.And>(Parse("#Work Learning today").Expression);
        Assert.Equal("Work Learning", Assert.IsType<FilterExpression.InProject>(and.Left).Name);
        Assert.IsType<FilterExpression.DueToday>(and.Right);
    }

    [Fact]
    public void A_word_after_a_known_project_is_a_term_of_its_own()
    {
        // "#Work today" is a project and a date, not a project called "Work today".
        var and = Assert.IsType<FilterExpression.And>(Parse("#Work today").Expression);
        Assert.Equal("Work", Assert.IsType<FilterExpression.InProject>(and.Left).Name);
        Assert.IsType<FilterExpression.DueToday>(and.Right);
    }

    [Fact]
    public void A_label_term_reads_its_name_spaces_and_all()
    {
        Assert.Equal("home", Assert.IsType<FilterExpression.HasLabel>(Parse("@home").Expression).Name);
        Assert.Equal("deep work", Assert.IsType<FilterExpression.HasLabel>(Parse("@deep work").Expression).Name);
    }

    [Theory]
    [InlineData("p1", Priority.P1)]
    [InlineData("p4", Priority.P4)]
    [InlineData("P2", Priority.P2)]
    public void Priorities_are_read_in_ui_terms(string query, Priority expected)
        => Assert.Equal(expected, Assert.IsType<FilterExpression.HasPriority>(Parse(query).Expression).Priority);

    [Fact]
    public void The_date_keywords_are_read()
    {
        Assert.IsType<FilterExpression.DueToday>(Parse("today").Expression);
        Assert.IsType<FilterExpression.Overdue>(Parse("overdue").Expression);
        Assert.IsType<FilterExpression.NoDate>(Parse("no date").Expression);
        Assert.IsType<FilterExpression.NoDate>(Parse("no due date").Expression);
        Assert.Equal(7, Assert.IsType<FilterExpression.NextDays>(Parse("next 7 days").Expression).Days);
        Assert.Equal(1, Assert.IsType<FilterExpression.NextDays>(Parse("next 1 day").Expression).Days);
    }

    [Fact]
    public void Search_takes_the_rest_of_the_run()
    {
        Assert.Equal("buy milk", Assert.IsType<FilterExpression.Search>(Parse("search: buy milk").Expression).Text);
        Assert.Equal("milk", Assert.IsType<FilterExpression.Search>(Parse("search:milk").Expression).Text);
    }

    [Fact]
    public void Search_stops_at_an_operator()
    {
        var and = Assert.IsType<FilterExpression.And>(Parse("search: buy milk & @home").Expression);
        Assert.Equal("buy milk", Assert.IsType<FilterExpression.Search>(and.Left).Text);
        Assert.Equal("home", Assert.IsType<FilterExpression.HasLabel>(and.Right).Name);
    }

    [Fact]
    public void An_exclamation_inside_a_word_is_text_not_negation()
        => Assert.Equal("sale!", Assert.IsType<FilterExpression.Search>(Parse("search: sale!").Expression).Text);

    // ---- Booleans ----------------------------------------------------------------------------------

    [Fact]
    public void And_or_and_not_are_read()
    {
        Assert.IsType<FilterExpression.And>(Parse("today & @home").Expression);
        Assert.IsType<FilterExpression.Or>(Parse("today | @home").Expression);
        Assert.IsType<FilterExpression.Or>(Parse("today, @home").Expression);
        Assert.IsType<FilterExpression.Not>(Parse("!@home").Expression);
    }

    [Fact]
    public void And_binds_tighter_than_or()
    {
        // a | b & c  ==  a | (b & c)
        var or = Assert.IsType<FilterExpression.Or>(Parse("overdue | today & @home").Expression);
        Assert.IsType<FilterExpression.Overdue>(or.Left);
        Assert.IsType<FilterExpression.And>(or.Right);
    }

    [Fact]
    public void Not_binds_tighter_than_and()
    {
        // !a & b  ==  (!a) & b
        var and = Assert.IsType<FilterExpression.And>(Parse("!today & @home").Expression);
        Assert.IsType<FilterExpression.Not>(and.Left);
        Assert.IsType<FilterExpression.HasLabel>(and.Right);
    }

    [Fact]
    public void Parentheses_override_precedence()
    {
        // (a | b) & c is an AND at the top, where a | b & c would be an OR.
        var and = Assert.IsType<FilterExpression.And>(Parse("(overdue | today) & @home").Expression);
        Assert.IsType<FilterExpression.Or>(and.Left);
        Assert.IsType<FilterExpression.HasLabel>(and.Right);
    }

    [Fact]
    public void Or_is_left_associative()
    {
        // a | b | c  ==  (a | b) | c
        var or = Assert.IsType<FilterExpression.Or>(Parse("today | overdue | @home").Expression);
        Assert.IsType<FilterExpression.Or>(or.Left);
        Assert.IsType<FilterExpression.HasLabel>(or.Right);
    }

    [Fact]
    public void Adjacent_terms_are_an_implicit_and()
    {
        var and = Assert.IsType<FilterExpression.And>(Parse("today p1").Expression);
        Assert.IsType<FilterExpression.DueToday>(and.Left);
        Assert.IsType<FilterExpression.HasPriority>(and.Right);
    }

    // ---- Refusals ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("assigned to: me")]     // collaborators are out of scope
    [InlineData("created before: -30 days")]
    [InlineData("tomorrow")]            // not in the normative date list
    [InlineData("next week")]
    [InlineData("next 0 days")]
    [InlineData("no")]
    [InlineData("#")]                   // names nothing
    [InlineData("@")]
    [InlineData("search:")]
    [InlineData("today &")]             // dangling operator
    [InlineData("| today")]
    [InlineData("(today")]              // unbalanced
    [InlineData("today)")]
    public void Anything_outside_the_grammar_is_refused(string query)
    {
        var parsed = Parse(query);

        Assert.False(parsed.IsSupported);
        Assert.Null(parsed.Expression);
        Assert.NotNull(parsed.Unsupported);
    }

    [Theory]
    [InlineData("(")]
    [InlineData("!")]
    [InlineData("today")]
    public void A_query_too_long_to_parse_safely_is_refused(string token)
    {
        // Nested brackets, stacked negations and a long flat run of terms all build something the
        // parser or the evaluator walks recursively. Deep enough and the stack goes — which can't
        // be caught, so the size has to be refused before anything walks it.
        var parsed = Parse(string.Join(' ', Enumerable.Repeat(token, 20_000)));

        Assert.False(parsed.IsSupported);
        Assert.NotNull(parsed.Unsupported);
        Assert.True(parsed.Unsupported.Length < 200, "the reported fragment is bounded too");
    }

    [Fact]
    public void A_refused_fragment_is_reported_on_one_line()
    {
        // A saved filter can carry newlines, and the notice showing this is a single line.
        var parsed = Parse("today &\r\nassigned to:\nme");

        Assert.False(parsed.IsSupported);
        Assert.DoesNotContain('\n', parsed.Unsupported!);
        Assert.DoesNotContain('\r', parsed.Unsupported!);
    }

    [Fact]
    public void A_refused_fragment_is_bounded_by_its_length_not_its_word_count()
    {
        // One enormous word is no fewer characters to render than a thousand small ones.
        var parsed = Parse(new string('x', 100_000) + " " + new string('y', 100_000));

        Assert.False(parsed.IsSupported);
        Assert.True(parsed.Unsupported!.Length < 200, $"fragment was {parsed.Unsupported.Length} characters");
    }

    [Fact]
    public void A_query_at_the_size_limit_still_parses()
    {
        // The ceiling has to leave room for any filter a person would actually write.
        Assert.True(Parse(string.Join(" & ", Enumerable.Repeat("today", 100))).IsSupported);
    }

    [Theory]
    [InlineData("next 3651 days")]
    [InlineData("next 999999999 days")]
    [InlineData("next 2147483647 days")]
    public void A_window_beyond_the_calendar_is_refused_not_evaluated(string query)
    {
        // Evaluating one of these walks the date past the end of the calendar and throws, from
        // inside the publish that renders the outline.
        Assert.False(Parse(query).IsSupported);
    }

    [Theory]
    [InlineData("No date")]
    [InlineData("NO DATE")]
    [InlineData("no DUE date")]
    public void The_date_keywords_are_read_however_they_are_capitalised(string query)
        => Assert.IsType<FilterExpression.NoDate>(Parse(query).Expression);

    [Theory]
    [InlineData("today &")]
    [InlineData("today !")]
    [InlineData("!")]
    [InlineData("(today")]
    public void A_refusal_names_something_rather_than_nothing(string query)
    {
        // "Termyn can't read this filter:" followed by a blank is no help at all.
        Assert.NotEmpty(Parse(query).Unsupported!);
    }

    [Fact]
    public void An_implicit_and_parses_the_same_as_an_explicit_one()
    {
        // Records compare structurally, so this catches an operand order or association change that
        // asserting on the node type alone would miss.
        Assert.Equal(Parse("today & p1").Expression, Parse("today p1").Expression);
        Assert.Equal(Parse("today | @home").Expression, Parse("today, @home").Expression);
        Assert.Equal(Parse("today").Expression, Parse("(today)").Expression);
    }

    [Fact]
    public void A_multi_word_name_the_account_does_not_have_refuses_the_query()
    {
        // The trailing word becomes a term of its own and fails, which is the honest outcome: with
        // no such project, guessing which words were meant to be the name would be a fiction.
        Assert.False(Parse("#Unknown Project").IsSupported);

        // A single unknown word is still a valid query — it just matches nothing.
        Assert.True(Parse("#Ghost").IsSupported);
    }

    [Fact]
    public void One_unreadable_term_refuses_the_whole_query()
    {
        // Dropping the part it can't read would return a plausible but wrong set of tasks.
        var parsed = Parse("today & assigned to: me");

        Assert.False(parsed.IsSupported);
    }

    [Fact]
    public void The_refused_fragment_is_reported()
        => Assert.Contains("tomorrow", Parse("today & tomorrow").Unsupported);

    private static FilterParse Parse(string query) => FilterParser.Parse(query, Vocabulary);
}
