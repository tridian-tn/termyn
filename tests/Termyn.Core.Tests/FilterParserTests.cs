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
        new(["Work", "Work Learning", "My Project"], ["home", "deep work"], ["Later", "Next Up"]);

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

    // Todoist's own spelling, and the one it uses for the four filters it gives every new account.
    // Reading only the short form refused a query the user never wrote and can't easily change.
    [InlineData("priority 1", Priority.P1)]
    [InlineData("Priority 4", Priority.P4)]
    public void Priorities_are_read_in_ui_terms(string query, Priority expected)
        => Assert.Equal(expected, Assert.IsType<FilterExpression.HasPriority>(Parse(query).Expression).Priority);

    [Theory]
    [InlineData("priority")]
    [InlineData("priority 5")]
    [InlineData("priority 0")]
    [InlineData("priority high")]
    public void A_priority_that_names_no_priority_is_refused(string query)
    {
        // Not silently treated as p4, and not read as the word "priority" on its own: a filter that
        // ran and answered with the wrong tasks is what this whole grammar exists to avoid.
        Assert.False(Parse(query).IsSupported);
    }

    [Fact]
    public void View_all_is_every_task_there_is()
    {
        // Another filter every account is given. It matches everything rather than standing for the
        // absence of a filter, so it composes with the rest of the grammar.
        Assert.IsType<FilterExpression.Everything>(Parse("view all").Expression);
        Assert.IsType<FilterExpression.Everything>(Parse("View All").Expression);

        var and = Assert.IsType<FilterExpression.And>(Parse("view all & p1").Expression);
        Assert.IsType<FilterExpression.Everything>(and.Left);
        Assert.IsType<FilterExpression.HasPriority>(and.Right);
    }

    [Theory]
    [InlineData("view")]
    [InlineData("view everything")]
    public void View_on_its_own_is_not_a_term(string query)
        => Assert.False(Parse(query).IsSupported);

    [Fact]
    public void Created_reads_the_day_a_task_was_added()
    {
        var today = Assert.IsType<FilterExpression.Created>(Parse("created: today").Expression);
        Assert.Equal(DayBound.On, today.Bound);
        Assert.Equal(FilterDay.Today, today.Day);

        var on = Assert.IsType<FilterExpression.Created>(Parse("created: 2026-01-31").Expression);
        Assert.Equal(new DateOnly(2026, 1, 31), on.Day.Absolute);
    }

    [Fact]
    public void Created_takes_a_bound_of_its_own()
    {
        var before = Assert.IsType<FilterExpression.Created>(Parse("created before: -30 days").Expression);
        Assert.Equal(DayBound.Before, before.Bound);
        Assert.Equal(-30, before.Day.DaysFromToday);

        var after = Assert.IsType<FilterExpression.Created>(Parse("created after: 2026-01-31").Expression);
        Assert.Equal(DayBound.After, after.Bound);
        Assert.Equal(new DateOnly(2026, 1, 31), after.Day.Absolute);

        // A window counted back stays relative, so a view left open overnight means the new today.
        Assert.Null(before.Day.Absolute);
    }

    [Fact]
    public void Created_composes_like_any_other_term()
    {
        var and = Assert.IsType<FilterExpression.And>(Parse("created: today & p1").Expression);
        Assert.IsType<FilterExpression.Created>(and.Left);
        Assert.IsType<FilterExpression.HasPriority>(and.Right);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("created:")]
    [InlineData("created: 31-01-2026")]
    [InlineData("created since: today")]
    [InlineData("created before:")]
    [InlineData("created before: 30")]
    public void A_created_term_it_cannot_read_is_refused_whole(string query)
    {
        // Half a dated term is the shape of mistake this grammar exists to avoid: "created:" read
        // as a bare word and the day dropped would answer with every task in the account.
        Assert.False(Parse(query).IsSupported);
    }

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

    // ---- The rest of the grammar -------------------------------------------------------------------

    [Fact]
    public void A_label_is_read_under_either_sigil()
    {
        // Todoist moved labels from "@" to "%" and is retiring the old one, so a saved filter can
        // be written either way and both have to keep working.
        Assert.Equal("home", Assert.IsType<FilterExpression.HasLabel>(Parse("%home").Expression).Name);
        Assert.Equal("deep work", Assert.IsType<FilterExpression.HasLabel>(Parse("%deep work").Expression).Name);
    }

    [Fact]
    public void A_section_term_reads_its_name()
    {
        Assert.Equal("Later", Assert.IsType<FilterExpression.InSection>(Parse("/Later").Expression).Name);

        // The same longest-known-name rule as a project: without the account's own names there is
        // nothing to say whether "today" is part of the section or a term of its own.
        Assert.Equal("Next Up", Assert.IsType<FilterExpression.InSection>(Parse("/Next Up").Expression).Name);

        var and = Assert.IsType<FilterExpression.And>(Parse("/Next Up today").Expression);
        Assert.Equal("Next Up", Assert.IsType<FilterExpression.InSection>(and.Left).Name);
        Assert.IsType<FilterExpression.DueToday>(and.Right);
    }

    [Fact]
    public void A_quoted_name_is_taken_whole()
    {
        // Which is how a name gets to hold a space without the parser having to recognise it, and
        // the only way one can hold a character the grammar would otherwise take for itself.
        Assert.Equal("My Project", Assert.IsType<FilterExpression.InProject>(Parse("""#"My Project" """).Expression).Name);
        Assert.Equal("Next Up", Assert.IsType<FilterExpression.InSection>(Parse("""/"Next Up" """).Expression).Name);

        var and = Assert.IsType<FilterExpression.And>(Parse("""#"Wed & Thu" & today""").Expression);
        Assert.Equal("Wed & Thu", Assert.IsType<FilterExpression.InProject>(and.Left).Name);
        Assert.IsType<FilterExpression.DueToday>(and.Right);
    }

    [Fact]
    public void A_backslash_hands_the_next_character_to_the_name()
    {
        var project = Assert.IsType<FilterExpression.InProject>(Parse("""#Books\&Papers""").Expression);

        Assert.Equal("Books&Papers", project.Name);
    }

    [Theory]
    [InlineData("""#"My Project""")]
    [InlineData("""#My Project\""")]
    [InlineData("\"")]
    [InlineData("\\")]
    public void A_query_that_stops_part_way_through_a_name_is_refused(string query)
    {
        // Rather than read as the shorter name it would otherwise leave behind: "#Foo\" is not a
        // query for #Foo, and answering it as though it were is the mistake all-or-nothing is for.
        Assert.False(Parse(query).IsSupported);
    }

    [Theory]
    [InlineData("no labels")]
    [InlineData("no label")]
    public void No_labels_is_read(string query)
        => Assert.IsType<FilterExpression.NoLabels>(Parse(query).Expression);

    [Fact]
    public void No_priority_is_the_lowest_one()
        => Assert.Equal(Priority.P4, Assert.IsType<FilterExpression.HasPriority>(Parse("no priority").Expression).Priority);

    [Theory]
    [InlineData("no time", typeof(FilterExpression.NoTime))]
    [InlineData("no deadline", typeof(FilterExpression.NoDeadline))]
    [InlineData("recurring", typeof(FilterExpression.Recurring))]
    [InlineData("subtask", typeof(FilterExpression.Subtask))]
    [InlineData("overdue", typeof(FilterExpression.Overdue))]
    [InlineData("over due", typeof(FilterExpression.Overdue))]
    [InlineData("od", typeof(FilterExpression.Overdue))]
    [InlineData("assigned", typeof(FilterExpression.Assigned))]
    [InlineData("shared", typeof(FilterExpression.Shared))]
    public void The_single_terms_are_read(string query, Type expected)
        => Assert.IsType(expected, Parse(query).Expression);

    [Theory]
    [InlineData("assigned to: me", typeof(FilterExpression.AssignedToMe))]
    [InlineData("ASSIGNED TO: ME", typeof(FilterExpression.AssignedToMe))]

    // The spelling Todoist puts in the saved filters it gives every account, so a real account
    // arrives with two filters written this way and neither may be refused.
    [InlineData(":to_me:", typeof(FilterExpression.AssignedToMe))]
    [InlineData(":to_others:", typeof(FilterExpression.AssignedToOthers))]
    [InlineData("assigned to: others", typeof(FilterExpression.AssignedToOthers))]
    [InlineData("assigned by: me", typeof(FilterExpression.AssignedByMe))]
    [InlineData("added by: me", typeof(FilterExpression.AddedByMe))]
    public void The_terms_about_people_are_read(string query, Type expected)
        => Assert.IsType(expected, Parse(query).Expression);

    [Fact]
    public void Assigned_on_its_own_still_reads_when_something_follows_it()
    {
        // The word starts two different terms, and the longer one only claims it when "to:" or
        // "by:" comes next — otherwise "assigned & p1" would be eaten looking for a person.
        var and = Assert.IsType<FilterExpression.And>(Parse("assigned & p1").Expression);

        Assert.IsType<FilterExpression.Assigned>(and.Left);
        Assert.IsType<FilterExpression.HasPriority>(and.Right);
    }

    [Fact]
    public void The_tasks_nobody_owns_are_the_negation_of_assigned()
    {
        // Todoist's own documented way of asking for them, so it has to compose with "!".
        var and = Assert.IsType<FilterExpression.And>(Parse("shared & !assigned").Expression);

        Assert.IsType<FilterExpression.Shared>(and.Left);
        Assert.IsType<FilterExpression.Assigned>(Assert.IsType<FilterExpression.Not>(and.Right).Operand);
    }

    [Theory]
    [InlineData("tomorrow", 1)]
    [InlineData("yesterday", -1)]
    public void Tomorrow_and_yesterday_are_days_either_side_of_today(string query, int offset)
    {
        var due = Assert.IsType<FilterExpression.Due>(Parse(query).Expression);

        Assert.Equal(DayBound.On, due.Bound);
        Assert.Equal(offset, due.Day.DaysFromToday);
    }

    [Fact]
    public void Next_week_is_a_day_the_account_names_rather_than_the_query()
    {
        var due = Assert.IsType<FilterExpression.Due>(Parse("due before: next week").Expression);

        Assert.Equal(DayBound.Before, due.Bound);
        Assert.Equal(DayAnchor.NextWeek, due.Day.Anchor);

        // Nothing is decided here. Which day it is comes off the account at evaluation time, so the
        // query mustn't carry a day of its own.
        Assert.Null(due.Day.Absolute);
        Assert.Null(due.Day.Weekday);
    }

    [Theory]
    [InlineData("due before: 1 week after next week", 7)]
    [InlineData("due before: 2 weeks after next week", 14)]
    public void The_far_end_of_a_week_is_counted_from_next_week(string query, int offset)
    {
        // Todoist's own way of saying "next week and no further" — the count is written as a count,
        // so it's read as one rather than taken literally as the only number allowed.
        var due = Assert.IsType<FilterExpression.Due>(Parse(query).Expression);

        Assert.Equal(DayAnchor.NextWeek, due.Day.Anchor);
        Assert.Equal(offset, due.Day.DaysFromToday);
    }

    [Fact]
    public void First_day_is_the_start_of_a_month()
        => Assert.Equal(
            DayAnchor.FirstOfMonth,
            Assert.IsType<FilterExpression.Due>(Parse("due before: first day").Expression).Day.Anchor);

    [Fact]
    public void The_week_reads_the_same_after_every_dated_term()
    {
        // The day is shared by all of them, so a form read after "due:" and not after "deadline:"
        // would be a gap nobody would think to look for.
        Assert.Equal(DayAnchor.NextWeek, Assert.IsType<FilterExpression.Deadline>(Parse("deadline: next week").Expression).Day.Anchor);
        Assert.Equal(DayAnchor.NextWeek, Assert.IsType<FilterExpression.Created>(Parse("created before: next week").Expression).Day.Anchor);
        Assert.Equal(DayAnchor.FirstOfMonth, Assert.IsType<FilterExpression.Deadline>(Parse("deadline before: first day").Expression).Day.Anchor);
    }

    [Fact]
    public void The_week_long_window_Todoist_writes_parses_whole()
    {
        // The filter its own help page gives for "due next week", and the reason the "N weeks after"
        // form exists at all. If either half were refused the whole query would be.
        Assert.True(Parse("(due: next week | due after: next week) & due before: 1 week after next week").IsSupported);
    }

    [Theory]
    [InlineData("due:", DayBound.On)]
    [InlineData("due before:", DayBound.Before)]
    [InlineData("due after:", DayBound.After)]
    [InlineData("date:", DayBound.On)]
    [InlineData("date before:", DayBound.Before)]
    public void A_due_term_reads_its_bound(string prefix, DayBound expected)
    {
        var due = Assert.IsType<FilterExpression.Due>(Parse($"{prefix} today").Expression);

        Assert.Equal(expected, due.Bound);
        Assert.Equal(FilterDay.Today, due.Day);
    }

    [Theory]
    [InlineData("deadline: today", DayBound.On)]
    [InlineData("deadline before: today", DayBound.Before)]
    [InlineData("deadline after: today", DayBound.After)]
    public void A_deadline_term_reads_its_bound(string query, DayBound expected)
        => Assert.Equal(expected, Assert.IsType<FilterExpression.Deadline>(Parse(query).Expression).Bound);

    [Theory]
    [InlineData("due: sat", DayOfWeek.Saturday)]
    [InlineData("due: saturday", DayOfWeek.Saturday)]
    [InlineData("due: SUN", DayOfWeek.Sunday)]
    [InlineData("due: thu", DayOfWeek.Thursday)]
    [InlineData("due: tuesday", DayOfWeek.Tuesday)]
    public void A_weekday_stays_a_weekday(string query, DayOfWeek expected)
    {
        // Rather than becoming an offset here: which day "sat" lands on depends on what today is,
        // and a query is parsed once and then evaluated for as long as the view is open.
        var due = Assert.IsType<FilterExpression.Due>(Parse(query).Expression);

        Assert.Equal(expected, due.Day.Weekday);
    }

    [Fact]
    public void A_day_can_be_written_the_long_way_round()
    {
        // "in 7 days" and "7 days" are the same day, and Todoist writes both.
        var plain = Assert.IsType<FilterExpression.Due>(Parse("due: 7 days").Expression);
        var spelled = Assert.IsType<FilterExpression.Due>(Parse("due: in 7 days").Expression);

        Assert.Equal(7, plain.Day.DaysFromToday);
        Assert.Equal(plain.Day, spelled.Day);
    }

    [Fact]
    public void A_bare_window_of_days_reads_as_a_window()
    {
        // Not as the single day at the end of it, which is what the same words mean after "due:".
        Assert.Equal(3, Assert.IsType<FilterExpression.NextDays>(Parse("3 days").Expression).Days);
        Assert.Equal(3, Assert.IsType<FilterExpression.LastDays>(Parse("-3 days").Expression).Days);
        Assert.Equal(1, Assert.IsType<FilterExpression.NextDays>(Parse("1 day").Expression).Days);
    }

    [Fact]
    public void A_bare_day_is_a_due_date()
    {
        Assert.Equal(new DateOnly(2026, 1, 31), Assert.IsType<FilterExpression.Due>(Parse("2026-01-31").Expression).Day.Absolute);
        Assert.Equal(DayOfWeek.Monday, Assert.IsType<FilterExpression.Due>(Parse("mon").Expression).Day.Weekday);
    }

    [Fact]
    public void The_new_terms_compose_like_any_other()
    {
        var and = Assert.IsType<FilterExpression.And>(Parse("#Work & /Later & !subtask").Expression);

        Assert.IsType<FilterExpression.InProject>(Assert.IsType<FilterExpression.And>(and.Left).Left);
        Assert.IsType<FilterExpression.InSection>(Assert.IsType<FilterExpression.And>(and.Left).Right);
        Assert.IsType<FilterExpression.Subtask>(Assert.IsType<FilterExpression.Not>(and.Right).Operand);
    }

    // ---- Refusals ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("assigned to: Sam")]    // collaborators are out of scope, so only "me" can be named
    [InlineData("assigned by: Sam")]
    [InlineData("added by: Sam")]
    [InlineData("assigned by: others")] // Todoist has no such term, and inventing one is a fiction
    [InlineData("added by: others")]
    [InlineData("added")]               // half a term
    [InlineData("workspace: Home")]
    [InlineData("next fortnight")]      // not a term, and "next" alone must not become one
    [InlineData("first")]
    [InlineData("first week")]
    [InlineData("1 week after next")]   // half the phrase
    [InlineData("1 week after today")]  // Todoist counts weeks from next week and nothing else
    [InlineData("0 weeks after next week")]
    [InlineData("next 0 days")]
    [InlineData("0 days")]
    [InlineData("no")]
    [InlineData("no idea")]
    [InlineData("#")]                   // names nothing
    [InlineData("@")]
    [InlineData("%")]
    [InlineData("/")]
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
        var parsed = Parse("today &\r\nworkspace:\nHome");

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
        var parsed = Parse("today & assigned to: Sam");

        Assert.False(parsed.IsSupported);
    }

    [Fact]
    public void The_refused_fragment_is_reported()
        => Assert.Contains("workspace:", Parse("today & workspace: Home").Unsupported);

    [Fact]
    public void A_refused_person_is_named_in_the_refusal()
    {
        // "Termyn can't read this filter: assigned" would send the reader looking at the wrong word.
        Assert.Contains("Sam", Parse("assigned to: Sam").Unsupported);
    }

    private static FilterParse Parse(string query) => FilterParser.Parse(query, Vocabulary);
}
