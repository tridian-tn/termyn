using Termyn.Core.Filters;
using Termyn.Core.Model;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

public class FilterEvaluatorTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    private static readonly Project[] Projects =
    [
        new() { Id = "work", Name = "Work" },
        new() { Id = "admin", Name = "Admin", ParentId = "work" },
        new() { Id = "deep", Name = "Deep", ParentId = "admin" },
        new() { Id = "home", Name = "Home" },
        new() { Id = "work2", Name = "Work" }, // Todoist allows two projects to share a name
    ];

    [Fact]
    public void A_project_term_matches_that_project_only()
    {
        Assert.True(Matches("#Home", Item(projectId: "home")));
        Assert.False(Matches("#Home", Item(projectId: "work")));
    }

    [Fact]
    public void A_project_term_matches_every_project_of_that_name()
    {
        // Two projects called Work are both "Work" to the user; matching one would hide the other.
        Assert.True(Matches("#Work", Item(projectId: "work")));
        Assert.True(Matches("#Work", Item(projectId: "work2")));
    }

    [Fact]
    public void A_single_hash_stops_at_the_project()
        => Assert.False(Matches("#Work", Item(projectId: "admin")));

    [Fact]
    public void A_double_hash_reaches_the_whole_subtree()
    {
        Assert.True(Matches("##Work", Item(projectId: "work")));
        Assert.True(Matches("##Work", Item(projectId: "admin")));
        Assert.True(Matches("##Work", Item(projectId: "deep"))); // a grandchild, two levels down
        Assert.False(Matches("##Work", Item(projectId: "home")));
    }

    [Fact]
    public void A_project_that_does_not_exist_matches_nothing()
        => Assert.False(Matches("#Ghost", Item(projectId: "work")));

    [Fact]
    public void A_task_with_no_project_is_in_no_project_term()
        => Assert.False(Matches("#Work", Item()));

    [Fact]
    public void Labels_match_by_name_ignoring_case()
    {
        Assert.True(Matches("@home", Item(labels: ["home"])));
        Assert.True(Matches("@home", Item(labels: ["Home"])));
        Assert.False(Matches("@home", Item(labels: ["work"])));
        Assert.False(Matches("@home", Item()));
    }

    [Fact]
    public void Priority_matches_in_ui_terms()
    {
        Assert.True(Matches("p1", Item(priority: Priority.P1)));
        Assert.False(Matches("p1", Item(priority: Priority.P4)));
    }

    [Fact]
    public void Today_is_the_day_itself_not_overdue_as_well()
    {
        // Unlike the Today smart view, which deliberately sweeps overdue in.
        Assert.True(Matches("today", Item(due: "2026-07-31")));
        Assert.False(Matches("today", Item(due: "2026-07-30")));
        Assert.False(Matches("today", Item(due: "2026-08-01")));
        Assert.False(Matches("today", Item()));
    }

    [Fact]
    public void Overdue_is_strictly_before_today()
    {
        Assert.True(Matches("overdue", Item(due: "2026-07-30")));
        Assert.False(Matches("overdue", Item(due: "2026-07-31")));
        Assert.False(Matches("overdue", Item()));
    }

    [Fact]
    public void No_date_means_the_field_is_absent()
    {
        Assert.True(Matches("no date", Item()));
        Assert.False(Matches("no date", Item(due: "2026-07-31")));

        // A due date Termyn can't read is still a date — calling it "no date" would be a lie.
        Assert.False(Matches("no date", Item(due: "every other Thursday")));
    }

    [Fact]
    public void Next_n_days_counts_from_today_inclusive()
    {
        Assert.True(Matches("next 7 days", Item(due: "2026-07-31")));  // today
        Assert.True(Matches("next 7 days", Item(due: "2026-08-06")));  // the seventh day
        Assert.False(Matches("next 7 days", Item(due: "2026-08-07"))); // one past the window
        Assert.False(Matches("next 7 days", Item(due: "2026-07-30"))); // overdue isn't upcoming
    }

    [Fact]
    public void The_largest_window_the_grammar_allows_evaluates_without_throwing()
    {
        // The parser caps the window precisely so this can't walk off the end of the calendar.
        // 3650 days from 2026-07-31 reaches 2036-07-27.
        Assert.True(Matches("next 3650 days", Item(due: "2036-07-27")));
        Assert.False(Matches("next 3650 days", Item(due: "2036-07-28")));
    }

    [Fact]
    public void Search_is_a_substring_of_the_content()
    {
        Assert.True(Matches("search: milk", Item(content: "Buy milk today")));
        Assert.True(Matches("search: MILK", Item(content: "Buy milk today")));
        Assert.False(Matches("search: milk", Item(content: "Buy bread")));
    }

    [Fact]
    public void A_utc_due_date_is_read_in_the_accounts_zone()
    {
        // 23:00 UTC on the 30th is already the 31st in Auckland, so it is due today there.
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var context = new FilterContext(Projects, Today, auckland);
        var parsed = FilterParser.Parse("today", Vocabulary());

        Assert.True(FilterEvaluator.Matches(parsed.Expression!, Item(due: "2026-07-30T23:00:00.000000Z"), context));
    }

    // ---- Booleans ----------------------------------------------------------------------------------

    [Fact]
    public void And_requires_both()
    {
        Assert.True(Matches("today & @home", Item(due: "2026-07-31", labels: ["home"])));
        Assert.False(Matches("today & @home", Item(due: "2026-07-31")));
        Assert.False(Matches("today & @home", Item(labels: ["home"])));
    }

    [Fact]
    public void Or_takes_either()
    {
        Assert.True(Matches("today | @home", Item(due: "2026-07-31")));
        Assert.True(Matches("today | @home", Item(labels: ["home"])));
        Assert.False(Matches("today | @home", Item()));
    }

    [Fact]
    public void A_comma_is_an_or()
        => Assert.True(Matches("today, @home", Item(labels: ["home"])));

    [Fact]
    public void Not_inverts()
    {
        Assert.True(Matches("!@home", Item()));
        Assert.False(Matches("!@home", Item(labels: ["home"])));
    }

    [Fact]
    public void Precedence_holds_when_evaluated()
    {
        // overdue | today & @home  ==  overdue | (today & @home)
        const string query = "overdue | today & @home";

        Assert.True(Matches(query, Item(due: "2026-07-30")));                     // overdue alone
        Assert.True(Matches(query, Item(due: "2026-07-31", labels: ["home"])));   // both halves of the AND
        Assert.False(Matches(query, Item(due: "2026-07-31")));                    // today without the label
    }

    [Fact]
    public void Parentheses_change_the_answer()
    {
        // The same terms, grouped the other way, admit a task the ungrouped form rejects.
        var task = Item(due: "2026-07-30", labels: ["home"]);

        Assert.True(Matches("(overdue | today) & @home", task));
        Assert.False(Matches("(overdue | today) & @home", Item(due: "2026-07-30")));
    }

    private static bool Matches(string query, TaskItem item)
    {
        var parsed = FilterParser.Parse(query, Vocabulary());
        Assert.True(parsed.IsSupported, $"query not supported: {query}");

        return FilterEvaluator.Matches(parsed.Expression!, item, new FilterContext(Projects, Today, TimeZoneInfo.Utc));
    }

    private static FilterVocabulary Vocabulary()
        => new(Projects.Select(p => p.Name), ["home"]);

    private static TaskItem Item(
        string content = "Task",
        string? projectId = null,
        string? due = null,
        Priority priority = Priority.P4,
        string[]? labels = null)
        => new()
        {
            Id = "i",
            Content = content,
            ProjectId = projectId,
            DueDate = due,
            Priority = priority,
            Labels = labels ?? [],
        };
}
