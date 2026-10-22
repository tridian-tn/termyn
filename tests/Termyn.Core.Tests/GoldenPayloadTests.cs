using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;

namespace Termyn.Core.Tests;

/// <summary>
/// Replays real sync and quick-add responses — recorded once from a live account and anonymised —
/// through the same parse, projection and recreate steps the app runs.
/// </summary>
/// <remarks>
/// Every other projection test builds its JSON by hand, so it asserts what the author imagined the
/// server sends. These assert what it actually sends: the fixtures keep the real field names, types
/// and nesting and change only the values, so a field Todoist renames or retypes fails a test here
/// rather than quietly matching nothing against a real account. That's the step the parser and
/// evaluator tests can't reach, and it's how fields slipped through theirs before.
/// </remarks>
public class GoldenPayloadTests
{
    [Fact]
    public async Task The_real_sync_body_parses_into_every_resource_type()
    {
        var response = await ReplaySync();

        Assert.True(response.FullSync);
        Assert.Equal("FIXTURE_SYNC_TOKEN", response.SyncToken);

        // A resource type gone missing — dropped by a rename or a reshape — shows up as a count that
        // no longer matches what was recorded.
        Assert.Equal(6, Changes(response, ResourceType.Items).Count);
        Assert.Equal(2, Changes(response, ResourceType.Projects).Count);
        Assert.Equal(2, Changes(response, ResourceType.Sections).Count);
        Assert.Equal(3, Changes(response, ResourceType.Labels).Count);
        Assert.Equal(2, Changes(response, ResourceType.Filters).Count);
        Assert.Single(Changes(response, ResourceType.Notes));
        Assert.Single(Changes(response, ResourceType.User));
        Assert.Single(Changes(response, ResourceType.UserPlanLimits));

        // Nothing projects to an empty shell: a wholesale rename of id or the naming field would
        // leave every row blank and this catches it in one go.
        foreach (var change in Changes(response, ResourceType.Items))
        {
            var item = Projections.ToTaskItem(change.Json);
            Assert.NotEqual(string.Empty, item.Id);
            Assert.NotEqual(string.Empty, item.Content);
        }
    }

    [Fact]
    public async Task A_real_task_projects_its_dates_labels_parent_and_priority()
    {
        var items = (await ReplaySync()).Changes
            .Where(c => c.ResourceType == ResourceType.Items)
            .ToDictionary(c => c.Id, c => Projections.ToTaskItem(c.Json));

        // A recurring due arrives as a nested object with the recurrence flag and the server's own
        // wording; a one-off carries the same shape with the flag clear.
        Assert.Equal("2026-11-04", items["ITEM002"].DueDate);
        Assert.True(items["ITEM002"].IsRecurring);
        Assert.Equal("every month", items["ITEM002"].DueText);
        Assert.False(items["ITEM003"].IsRecurring);

        // The deadline is a field of its own, separate from the due date, and the due stays null.
        Assert.Equal("2026-09-18", items["ITEM004"].Deadline);
        Assert.Null(items["ITEM004"].DueDate);

        Assert.Equal(["label1"], items["ITEM005"].Labels);
        Assert.Equal("ITEM001", items["ITEM006"].ParentId);

        // Todoist's priority runs the other way to the one shown: its 1 is the app's lowest.
        Assert.Equal(Priority.P4, items["ITEM001"].Priority);
        Assert.Equal(Priority.P1, items["ITEM002"].Priority);

        // A real creation stamp, read off whichever field name the account sends it under.
        Assert.False(string.IsNullOrEmpty(items["ITEM001"].AddedAt));
    }

    [Fact]
    public async Task A_real_project_section_label_and_filter_project_their_fields()
    {
        var response = await ReplaySync();

        var inbox = Projections.ToProject(Changes(response, ResourceType.Projects).Single(c => c.Id == "PROJ002").Json);
        // The live payload names the inbox flag inbox_project, not the is_inbox_project the typed
        // tests reach for — the projection takes either, and only a recorded body proves it.
        Assert.True(inbox.IsInboxProject);
        Assert.False(inbox.IsShared);
        Assert.Equal("charcoal", inbox.Color);
        Assert.NotEqual(string.Empty, inbox.Name);

        var section = Projections.ToSection(Changes(response, ResourceType.Sections).First().Json);
        Assert.Equal("PROJ002", section.ProjectId);
        Assert.NotEqual(string.Empty, section.Name);

        var label = Projections.ToLabel(Changes(response, ResourceType.Labels).First().Json);
        Assert.NotEqual(string.Empty, label.Name);
        Assert.Equal("charcoal", label.Color);

        var filter = Projections.ToFilter(Changes(response, ResourceType.Filters).First().Json);
        Assert.NotEqual(string.Empty, filter.Query);
        Assert.NotEqual(string.Empty, filter.Name);
    }

    [Fact]
    public async Task The_real_user_resource_yields_the_account_id_and_date_settings()
    {
        var user = (await ReplaySync()).Changes.Single(c => c.ResourceType == ResourceType.User).Json;

        Assert.Equal("USER001", Projections.ToUserId(user));

        var settings = Projections.ToDateSettings(user);
        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), settings.TimeZone);
        Assert.True(settings.DayFirst);              // date_format 0
        Assert.Equal(DayOfWeek.Monday, settings.WeekStart);
        Assert.False(settings.InlineDatesOff);       // smart dates on unless the account turns them off
    }

    [Fact]
    public async Task The_real_plan_limits_carry_the_reminder_time_allowance()
    {
        var limits = Projections.ToPlanLimits(
            (await ReplaySync()).Changes.Single(c => c.ResourceType == ResourceType.UserPlanLimits).Json);

        Assert.NotEqual(string.Empty, limits.PlanName);
        Assert.True(limits.Reminders);
        // The time-reminder allowance is a property of the plan, so it rides on the limits rather
        // than on any one task.
        Assert.True(limits.MaxTimeReminders > 0);
    }

    [Fact]
    public async Task A_real_comment_projects_its_text_and_the_task_it_hangs_off()
    {
        var note = (await ReplaySync()).Changes.Single(c => c.ResourceType == ResourceType.Notes).Json;

        var comment = Projections.ToComment(note);
        Assert.Equal("ITEM001", comment.ItemId);
        Assert.NotEqual(string.Empty, comment.Content);
        Assert.False(string.IsNullOrEmpty(comment.PostedAt));
    }

    [Fact]
    public async Task A_real_task_survives_the_recreate_round_trip()
    {
        var item = (await ReplaySync()).Changes.First(c => c.Id == "ITEM002").Json;
        var original = Projections.ToTaskItem(item);

        // What a deleted task is rebuilt from when its delete is undone.
        var recreate = ItemFields.ForRecreate(item);

        // Server-owned fields are left behind — sending them back would be claiming values the
        // client doesn't own.
        Assert.False(recreate.ContainsKey("id"));
        Assert.False(recreate.ContainsKey("user_id"));
        Assert.False(recreate.ContainsKey("added_at"));
        Assert.False(recreate.ContainsKey("checked"));

        // What's kept still describes the same task when it's read back the way a fresh sync would.
        var rebuilt = Projections.ToTaskItem(recreate);
        Assert.Equal(original.Content, rebuilt.Content);
        Assert.Equal(original.DueDate, rebuilt.DueDate);
        Assert.Equal(original.DueText, rebuilt.DueText);
        Assert.Equal(original.IsRecurring, rebuilt.IsRecurring);
        Assert.Equal(original.Priority, rebuilt.Priority);
    }

    // ---- replay plumbing -----------------------------------------------------------------------

    private static Task<SyncResponse> ReplaySync()
        => ClientReturning(Fixture("sync-full.json"))
            .SyncAsync("tok", "*", ResourceType.All, [], TestContext.Current.CancellationToken);

    private static IReadOnlyList<ResourceChange> Changes(SyncResponse response, string type)
        => response.Changes.Where(c => c.ResourceType == type).ToList();

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static TodoistApiClient ClientReturning(string json)
        => new(new HttpClient(new StubHandler(json)));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }
}
