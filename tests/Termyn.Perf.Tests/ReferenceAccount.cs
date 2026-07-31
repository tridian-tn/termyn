using Termyn.Core.Model;
using Termyn.Core.Sync;

namespace Termyn.Perf.Tests;

/// <summary>
/// Builds the account the performance budget is defined against: around five thousand active tasks,
/// spread over projects, sections and labels, with the sub-task nesting and the unmodelled fields a
/// real account carries — those are what the projection actually has to walk.
/// </summary>
internal static class ReferenceAccount
{
    public const int Tasks = 5_000;
    public const int Projects = 40;
    public const int SectionsPerProject = 4;
    public const int Labels = 25;

    /// <summary>Writes the account into a store, in one transaction, as a sync would.</summary>
    /// <param name="tasks">How many tasks to seed; the scaling checks vary this.</param>
    /// <param name="projects">How many projects to spread them over; the scaling checks vary this too.</param>
    public static void Seed(ISnapshotStore store, int tasks = Tasks, int projects = Projects)
    {
        var resources = new List<StoredResource>(tasks + (projects * (SectionsPerProject + 1)) + Labels + 2);

        resources.Add(new StoredResource(ResourceType.User, ResourceType.User,
            """{"id":"u1","full_name":"Reference","tz_info":{"timezone":"Europe/London"}}"""));
        resources.Add(new StoredResource(ResourceType.UserPlanLimits, ResourceType.UserPlanLimits,
            """{"current":{"plan_name":"pro","reminders":true,"max_reminders_time":100}}"""));

        for (var p = 0; p < projects; p++)
        {
            var id = $"p{p}";
            var parent = p >= 10 ? $",\"parent_id\":\"p{p % 10}\"" : string.Empty;
            var inbox = p == 0 ? ",\"is_inbox_project\":true" : string.Empty;
            resources.Add(new StoredResource(ResourceType.Projects, id,
                $$"""{"id":"{{id}}","name":"Project {{p}}","child_order":{{p}}{{parent}}{{inbox}},"color":"charcoal","view_style":"list"}"""));

            for (var s = 0; s < SectionsPerProject; s++)
            {
                var sectionId = $"s{p}-{s}";
                resources.Add(new StoredResource(ResourceType.Sections, sectionId,
                    $$"""{"id":"{{sectionId}}","name":"Section {{s}}","project_id":"{{id}}","section_order":{{s}}}"""));
            }
        }

        for (var l = 0; l < Labels; l++)
        {
            resources.Add(new StoredResource(ResourceType.Labels, $"l{l}",
                $$"""{"id":"l{{l}}","name":"label{{l}}","item_order":{{l}},"color":"berry_red"}"""));
        }

        for (var i = 0; i < tasks; i++)
        {
            var project = i % projects;
            var section = i % SectionsPerProject;

            // Every fourth task is a sub-task of the one four places before it, so the projection has
            // a tree to flatten rather than a flat list to copy.
            var parent = i % 4 == 3 && i >= 4 ? $",\"parent_id\":\"i{i - 3}\"" : string.Empty;

            // Half are dated, spread across a fortnight either side of the reference date, so Today
            // and Upcoming both have real work to do.
            var due = i % 2 == 0
                ? $",\"due\":{{\"date\":\"{Today.AddDays((i % 28) - 14):yyyy-MM-dd}\",\"string\":\"a date\",\"lang\":\"en\",\"is_recurring\":{(i % 10 == 0 ? "true" : "false")}}}"
                : string.Empty;

            var labels = $"[\"label{i % Labels}\",\"label{(i + 7) % Labels}\"]";

            resources.Add(new StoredResource(ResourceType.Items, $"i{i}",
                $$"""
                {"id":"i{{i}}","content":"Task {{i}} — something to do","description":"","project_id":"p{{project}}",
                "section_id":"s{{project}}-{{section}}","child_order":{{i}},"priority":{{(i % 4) + 1}},
                "labels":{{labels}},"checked":false,"is_deleted":false,"added_at":"2026-01-01T00:00:00Z",
                "added_by_uid":"u1","note_count":0,"day_order":-1,"is_collapsed":false{{parent}}{{due}}}
                """.ReplaceLineEndings(string.Empty)));
        }

        store.SaveSync(resources, [], "seeded-token");
    }

    /// <summary>The date the seeded due dates are spread around.</summary>
    public static readonly DateOnly Today = new(2026, 7, 31);
}
