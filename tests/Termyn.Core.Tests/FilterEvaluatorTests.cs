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
        new() { Id = "team", Name = "Team", IsShared = true },
    ];

    /// <summary>The account these tests run as. Everyone else is somebody else.</summary>
    private const string Me = "u-me";

    /// <summary>
    /// Sections across two projects. "Later" is in both on purpose: section names repeat far more
    /// than project names do, and a term naming one has to mean every one of them.
    /// </summary>
    private static readonly Section[] Sections =
    [
        new() { Id = "later-work", Name = "Later", ProjectId = "work" },
        new() { Id = "later-home", Name = "Later", ProjectId = "home" },
        new() { Id = "meetings", Name = "Meetings", ProjectId = "work" },
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

        // The long form is the same term, so it has to reach the same answer and not merely parse.
        Assert.True(Matches("priority 1", Item(priority: Priority.P1)));
        Assert.False(Matches("priority 1", Item(priority: Priority.P4)));
    }

    [Fact]
    public void View_all_takes_everything_including_what_nothing_else_would()
    {
        // A task with no project, no labels, no priority to speak of and no date matches nothing
        // else in the grammar — which is the case that tells "everything" apart from "anything".
        Assert.True(Matches("view all", Item()));
        Assert.True(Matches("view all", Item(projectId: "home", priority: Priority.P1, due: "2026-07-31")));

        // And it composes rather than swallowing what it is combined with.
        Assert.True(Matches("view all & p1", Item(priority: Priority.P1)));
        Assert.False(Matches("view all & p1", Item(priority: Priority.P4)));
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
    public void Created_is_the_day_the_task_was_added()
    {
        Assert.True(Matches("created: today", Item(added: "2026-07-31T09:15:00.000000Z")));
        Assert.False(Matches("created: today", Item(added: "2026-07-30T09:15:00.000000Z")));

        Assert.True(Matches("created: 2026-07-30", Item(added: "2026-07-30T09:15:00.000000Z")));
        Assert.False(Matches("created: 2026-07-30", Item(added: "2026-07-31T09:15:00.000000Z")));
    }

    [Fact]
    public void Created_before_and_after_are_strict_and_counted_from_today()
    {
        Assert.True(Matches("created before: today", Item(added: "2026-07-30T09:15:00.000000Z")));
        Assert.False(Matches("created before: today", Item(added: "2026-07-31T09:15:00.000000Z")));

        // -30 days from 2026-07-31 is 2026-07-01, and the bound excludes the day itself.
        Assert.True(Matches("created before: -30 days", Item(added: "2026-06-30T09:15:00.000000Z")));
        Assert.False(Matches("created before: -30 days", Item(added: "2026-07-01T09:15:00.000000Z")));

        Assert.True(Matches("created after: -30 days", Item(added: "2026-07-02T09:15:00.000000Z")));
        Assert.False(Matches("created after: -30 days", Item(added: "2026-07-01T09:15:00.000000Z")));
    }

    [Fact]
    public void A_task_with_no_creation_date_is_in_no_created_term()
    {
        // Not in "before" either, which is the tempting one — an unknown day isn't an early day.
        Assert.False(Matches("created: today", Item()));
        Assert.False(Matches("created before: today", Item()));
        Assert.False(Matches("created after: today", Item()));
    }

    [Fact]
    public void The_day_a_task_was_added_is_read_in_the_accounts_zone()
    {
        // 23:00 UTC on the 30th is already the 31st in Auckland, so it was added today there. This
        // is the case created: exists for — everything Todoist stamps a creation with is UTC.
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var context = new FilterContext(Projects, Today, auckland);
        var parsed = FilterParser.Parse("created: today", Vocabulary());

        Assert.True(FilterEvaluator.Matches(parsed.Expression!, Item(added: "2026-07-30T23:00:00.000000Z"), context));
        Assert.False(FilterEvaluator.Matches(parsed.Expression!, Item(added: "2026-07-30T09:00:00.000000Z"), context));
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

    // ---- Where a task is ---------------------------------------------------------------------------

    [Fact]
    public void A_section_term_matches_every_section_of_that_name()
    {
        // Two projects both have a "Later", and a filter naming it means both. Narrowing it down to
        // one is what putting a project alongside is for.
        Assert.True(Matches("/Later", Item(projectId: "work", sectionId: "later-work")));
        Assert.True(Matches("/Later", Item(projectId: "home", sectionId: "later-home")));
        Assert.False(Matches("/Later", Item(projectId: "work", sectionId: "meetings")));

        Assert.True(Matches("#Work & /Later", Item(projectId: "work", sectionId: "later-work")));
        Assert.False(Matches("#Work & /Later", Item(projectId: "home", sectionId: "later-home")));
    }

    [Fact]
    public void A_task_in_no_section_is_in_no_section_term()
        => Assert.False(Matches("/Later", Item(projectId: "work")));

    [Fact]
    public void A_subtask_is_one_filed_under_another_task()
    {
        Assert.True(Matches("subtask", Item(parentId: "parent")));
        Assert.False(Matches("subtask", Item()));
        Assert.True(Matches("!subtask", Item()));
    }

    [Fact]
    public void Recurring_is_what_the_server_says_it_is()
    {
        Assert.True(Matches("recurring", Item(due: "2026-07-31", recurring: true)));
        Assert.False(Matches("recurring", Item(due: "2026-07-31")));
    }

    // ---- Labels and priority -----------------------------------------------------------------------

    [Fact]
    public void The_label_sigils_are_both_read()
    {
        // Todoist writes "%home" now and is retiring "@home", so an account can hold either.
        Assert.True(Matches("%home", Item(labels: ["home"])));
        Assert.True(Matches("@home", Item(labels: ["home"])));
    }

    [Fact]
    public void No_labels_is_a_task_carrying_none()
    {
        Assert.True(Matches("no labels", Item()));
        Assert.False(Matches("no labels", Item(labels: ["home"])));
    }

    [Fact]
    public void No_priority_is_the_lowest_one()
    {
        // Todoist gives every task a priority and calls the bottom of the four none.
        Assert.True(Matches("no priority", Item(priority: Priority.P4)));
        Assert.False(Matches("no priority", Item(priority: Priority.P1)));
    }

    // ---- Days --------------------------------------------------------------------------------------

    [Fact]
    public void Tomorrow_and_yesterday_are_the_days_either_side()
    {
        Assert.True(Matches("tomorrow", Item(due: "2026-08-01")));
        Assert.False(Matches("tomorrow", Item(due: "2026-07-31")));

        Assert.True(Matches("yesterday", Item(due: "2026-07-30")));
        Assert.False(Matches("yesterday", Item(due: "2026-07-31")));
    }

    [Fact]
    public void Overdue_is_written_three_ways_and_means_one()
    {
        foreach (var query in new[] { "overdue", "over due", "od" })
        {
            Assert.True(Matches(query, Item(due: "2026-07-30")), query);
            Assert.False(Matches(query, Item(due: "2026-07-31")), query);
        }
    }

    [Fact]
    public void A_due_term_reads_the_day_the_task_falls_on()
    {
        Assert.True(Matches("due: today", Item(due: "2026-07-31")));
        Assert.True(Matches("due before: today", Item(due: "2026-07-30")));
        Assert.True(Matches("due after: today", Item(due: "2026-08-01")));

        Assert.False(Matches("due before: today", Item(due: "2026-07-31")));
        Assert.False(Matches("due after: today", Item(due: "2026-07-31")));
    }

    [Fact]
    public void Date_is_the_older_name_for_the_same_term()
    {
        Assert.True(Matches("date: 2026-08-01", Item(due: "2026-08-01")));
        Assert.False(Matches("date: 2026-08-01", Item(due: "2026-07-31")));
    }

    [Fact]
    public void A_weekday_is_the_coming_one()
    {
        // Today is a Friday, so "sat" is tomorrow and "wed" is five days out — not the Wednesday
        // two days back. A weekday always looks forward.
        Assert.True(Matches("due: sat", Item(due: "2026-08-01")));
        Assert.True(Matches("due: saturday", Item(due: "2026-08-01")));
        Assert.True(Matches("due: wed", Item(due: "2026-08-05")));
        Assert.False(Matches("due: wed", Item(due: "2026-07-29")));
    }

    [Fact]
    public void A_weekday_that_is_today_is_today()
    {
        // The coming Friday, when today is one, is this one — the same reading quick add gives it.
        Assert.True(Matches("due: fri", Item(due: "2026-07-31")));
        Assert.False(Matches("due: fri", Item(due: "2026-08-07")));
    }

    [Fact]
    public void A_bare_day_is_the_day_it_is_due()
    {
        Assert.True(Matches("2026-08-01", Item(due: "2026-08-01")));
        Assert.True(Matches("mon", Item(due: "2026-08-03")));
        Assert.False(Matches("mon", Item(due: "2026-08-04")));
    }

    [Fact]
    public void A_window_of_days_counts_today_at_both_ends()
    {
        // "3 days" is today and the two after it; "-3 days" is today and the two before. The day
        // they share is today, which is what makes each of them three days rather than four.
        Assert.True(Matches("3 days", Item(due: "2026-07-31")));
        Assert.True(Matches("3 days", Item(due: "2026-08-02")));
        Assert.False(Matches("3 days", Item(due: "2026-08-03")));
        Assert.False(Matches("3 days", Item(due: "2026-07-30")));

        Assert.True(Matches("-3 days", Item(due: "2026-07-31")));
        Assert.True(Matches("-3 days", Item(due: "2026-07-29")));
        Assert.False(Matches("-3 days", Item(due: "2026-07-28")));
        Assert.False(Matches("-3 days", Item(due: "2026-08-01")));
    }

    [Fact]
    public void No_time_is_a_date_without_an_hour()
    {
        // And a task with no date at all, which has no hour either — Todoist's own way of asking
        // for "a date and a time" is "!no date & !no time", which needs both halves only because
        // this one lets the dateless through.
        Assert.True(Matches("no time", Item(due: "2026-07-31")));
        Assert.True(Matches("no time", Item()));
        Assert.False(Matches("no time", Item(due: "2026-07-31T09:00:00")));

        Assert.True(Matches("!no date & !no time", Item(due: "2026-07-31T09:00:00")));
        Assert.False(Matches("!no date & !no time", Item(due: "2026-07-31")));
    }

    // ---- Deadlines ---------------------------------------------------------------------------------

    [Fact]
    public void A_deadline_term_reads_the_day_it_has_to_be_done_by()
    {
        Assert.True(Matches("deadline: today", Item(deadline: "2026-07-31")));
        Assert.True(Matches("deadline before: today", Item(deadline: "2026-07-30")));
        Assert.True(Matches("deadline after: today", Item(deadline: "2026-08-01")));
        Assert.False(Matches("deadline: today", Item(deadline: "2026-08-01")));
    }

    [Fact]
    public void A_deadline_is_read_apart_from_the_due_date()
    {
        // The two are different dates on the same task, and neither answers for the other.
        var task = Item(due: "2026-08-15", deadline: "2026-07-31");

        Assert.True(Matches("deadline: today", task));
        Assert.False(Matches("today", task));
    }

    [Fact]
    public void No_deadline_is_a_task_that_has_not_got_one()
    {
        Assert.True(Matches("no deadline", Item(due: "2026-07-31")));
        Assert.False(Matches("no deadline", Item(deadline: "2026-07-31")));
        Assert.True(Matches("!no deadline", Item(deadline: "2026-07-31")));
    }

    [Fact]
    public void A_task_with_no_deadline_is_in_no_deadline_term()
    {
        // Not in "before" either, the same way an unknown creation date isn't an early one.
        Assert.False(Matches("deadline: today", Item()));
        Assert.False(Matches("deadline before: today", Item()));
        Assert.False(Matches("deadline after: today", Item()));
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

    // ---- Who a task is for ------------------------------------------------------------------------

    [Fact]
    public void Assigned_is_anybody_at_all()
    {
        Assert.True(Matches("assigned", Item(responsible: Me)));
        Assert.True(Matches("assigned", Item(responsible: "u-sam")));
        Assert.False(Matches("assigned", Item()));

        // How Todoist writes "in a shared list and nobody has picked it up".
        Assert.True(Matches("!assigned", Item()));
    }

    [Fact]
    public void Assigned_to_me_is_this_account_and_nobody_else()
    {
        Assert.True(Matches("assigned to: me", Item(responsible: Me)));
        Assert.False(Matches("assigned to: me", Item(responsible: "u-sam")));
        Assert.False(Matches("assigned to: me", Item()));
    }

    [Fact]
    public void Assigned_to_others_needs_an_assignment_as_well_as_somebody_else()
    {
        // An unassigned task isn't assigned to others, which is the half that's easy to drop.
        Assert.True(Matches("assigned to: others", Item(responsible: "u-sam")));
        Assert.False(Matches("assigned to: others", Item(responsible: Me)));
        Assert.False(Matches("assigned to: others", Item()));
    }

    [Fact]
    public void Who_assigned_it_and_who_added_it_are_read_off_their_own_fields()
    {
        // Three fields, three questions. A task somebody else handed me was assigned by them and
        // added by them, and is still assigned to me — so no one of these answers another.
        var handedToMe = Item(responsible: Me, assignedBy: "u-sam", addedBy: "u-sam");

        Assert.True(Matches("assigned to: me", handedToMe));
        Assert.False(Matches("assigned by: me", handedToMe));
        Assert.False(Matches("added by: me", handedToMe));

        var handedOut = Item(responsible: "u-sam", assignedBy: Me, addedBy: Me);

        Assert.False(Matches("assigned to: me", handedOut));
        Assert.True(Matches("assigned by: me", handedOut));
        Assert.True(Matches("added by: me", handedOut));
    }

    [Fact]
    public void Shared_is_the_project_the_task_sits_in()
    {
        Assert.True(Matches("shared", Item(projectId: "team")));
        Assert.False(Matches("shared", Item(projectId: "work")));

        // A task with no project can't be in a shared one.
        Assert.False(Matches("shared", Item()));
    }

    [Fact]
    public void With_no_account_synced_the_terms_naming_me_match_nothing()
    {
        // The caller refuses a filter like this outright rather than running it. If one ever gets
        // this far, matching nothing is the harmless answer — matching everything assigned is not.
        Assert.False(Matches("assigned to: me", Item(responsible: Me), userId: null));
        Assert.False(Matches("assigned to: others", Item(responsible: "u-sam"), userId: null));
        Assert.False(Matches("assigned by: me", Item(assignedBy: Me), userId: null));
        Assert.False(Matches("added by: me", Item(addedBy: Me), userId: null));

        // Neither of these asks who the account is, so both still answer.
        Assert.True(Matches("assigned", Item(responsible: "u-sam"), userId: null));
        Assert.True(Matches("shared", Item(projectId: "team"), userId: null));
    }

    [Fact]
    public void Parentheses_change_the_answer()
    {
        // The same terms, grouped the other way, admit a task the ungrouped form rejects.
        var task = Item(due: "2026-07-30", labels: ["home"]);

        Assert.True(Matches("(overdue | today) & @home", task));
        Assert.False(Matches("(overdue | today) & @home", Item(due: "2026-07-30")));
    }

    private static bool Matches(string query, TaskItem item, string? userId = Me)
    {
        var parsed = FilterParser.Parse(query, Vocabulary());
        Assert.True(parsed.IsSupported, $"query not supported: {query}");

        var context = new FilterContext(Projects, Today, TimeZoneInfo.Utc, Sections, userId);
        return FilterEvaluator.Matches(parsed.Expression!, item, context);
    }

    private static FilterVocabulary Vocabulary()
        => new(Projects.Select(p => p.Name), ["home"], Sections.Select(s => s.Name));

    private static TaskItem Item(
        string content = "Task",
        string? projectId = null,
        string? sectionId = null,
        string? parentId = null,
        string? due = null,
        string? deadline = null,
        bool recurring = false,
        Priority priority = Priority.P4,
        string[]? labels = null,
        string? added = null,
        string? responsible = null,
        string? assignedBy = null,
        string? addedBy = null)
        => new()
        {
            Id = "i",
            Content = content,
            ProjectId = projectId,
            SectionId = sectionId,
            ParentId = parentId,
            DueDate = due,
            Deadline = deadline,
            IsRecurring = recurring,
            Priority = priority,
            Labels = labels ?? [],
            AddedAt = added,
            ResponsibleUid = responsible,
            AssignedByUid = assignedBy,
            AddedByUid = addedBy,
        };
}
