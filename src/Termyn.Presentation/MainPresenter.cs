using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Filters;
using Termyn.Core.Model;
using Termyn.Core.Sync;

namespace Termyn.Presentation;

/// <summary>A single row rendered in the outline. <paramref name="Depth"/> is its indent level.</summary>
public sealed record TaskRow(
    string Id,
    string Content,
    Priority Priority,
    string Project,
    string Due,
    IReadOnlyList<string> Labels,
    int Depth = 0);

/// <summary>
/// What the local parser made of some capture text, and how its names resolved.
/// <paramref name="Parse"/> already carries the text the task would be created with.
/// </summary>
public sealed record CapturePreview(QuickAddParse Parse, bool ProjectResolved, bool SectionResolved);

/// <summary>
/// Drives the sidebar and the outline: exposes the engine's model as rows and turns user intents
/// into engine operations. UI-framework agnostic, so another platform's view can bind to the same
/// presenter.
/// </summary>
public sealed class MainPresenter
{
    private readonly SyncEngine _engine;
    private readonly QuickAddParser _parser;

    /// <summary>
    /// Serialises publishing. Intents run on the UI thread while the background sync publishes from
    /// its worker; without this an older snapshot can be assigned after a newer one and put a
    /// completed task back on screen.
    /// </summary>
    private readonly Lock _publishing = new();

    private IReadOnlyList<TaskRow> _allRows = [];
    private IReadOnlyList<TaskRow> _searchableRows = [];

    public MainPresenter(SyncEngine engine, QuickAddParser parser)
    {
        _engine = engine;
        _parser = parser;
        Publish(); // reflect whatever the engine already has loaded
    }

    /// <summary>Raised whenever the sidebar, rows or status have been refreshed.</summary>
    public event Action? RowsChanged;

    public IReadOnlyList<TaskRow> Rows { get; private set; } = [];

    public IReadOnlyList<SidebarNode> Sidebar { get; private set; } = [];

    public ViewSelection Selection { get; private set; } = ViewSelection.Default;

    public string Status { get; private set; } = string.Empty;

    public bool IsOffline { get; private set; }

    public bool CanUndo => _engine.CanUndo;

    /// <summary>Free-text filter applied to the rendered rows.</summary>
    public string SearchQuery { get; private set; } = string.Empty;

    /// <summary>
    /// The query of the selected saved filter when Termyn can't evaluate it, so the view can offer
    /// to open it in Todoist. Null whenever the current view is showing a real answer.
    /// </summary>
    public string? UnsupportedFilter { get; private set; }

    /// <summary>Every label in the account, in the order the sidebar lists them.</summary>
    public IReadOnlyList<Label> Labels { get; private set; } = [];

    /// <summary>
    /// Publishes the cached model immediately, then reconciles with the server and publishes again.
    /// Losing the network leaves the cached rows on screen; only a rejected token propagates.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsOffline = false;
        Publish();
        await SyncAsync(ct);
    }

    /// <summary>Reconciles with the server and republishes, keeping the current view if offline.</summary>
    /// <returns>True when writes are still queued, so the caller can come back sooner.</returns>
    public async Task<bool> SyncAsync(CancellationToken ct = default)
    {
        try
        {
            await _engine.SyncAsync(ct);
            IsOffline = false;
        }
        catch (TodoistNetworkException)
        {
            IsOffline = true;
        }

        Publish();

        // Only ask for another round while the network is answering, or the loop would spin.
        return !IsOffline && _engine.PendingCount > 0;
    }

    /// <summary>
    /// Captures a task from quick-add text. Online, the server parses it so the result matches the
    /// web app; offline, the bounded local grammar is used and the task syncs later.
    /// </summary>
    public async Task CaptureAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var captured = await _engine.QuickAddOnlineAsync(text, ct);
        IsOffline = !captured;

        if (!captured)
        {
            var (parse, projectId, sectionId) = Resolve(text);

            // With nothing named at all, a capture belongs wherever the user is looking. Applied
            // together: a section from one view with a project named in the text is not a real place.
            if (projectId is null && sectionId is null)
            {
                projectId = Selection.ProjectId ?? SectionProjectId();
                sectionId = Selection.SectionId;
            }

            _engine.AddItem(ItemFields.ForAdd(parse, projectId, sectionId));
        }

        Publish();
    }

    /// <summary>Shows what the local parser understood, for the capture preview.</summary>
    public CapturePreview Preview(string text)
    {
        var (parse, projectId, sectionId) = Resolve(text);
        return new CapturePreview(
            parse,
            parse.ProjectName is null || projectId is not null,
            parse.SectionName is null || sectionId is not null);
    }

    // ---- Navigation ----------------------------------------------------------------------------

    public void Select(ViewSelection selection)
    {
        Selection = selection;
        Publish();
    }

    public void Search(string query)
    {
        SearchQuery = query ?? string.Empty;
        Republish();
    }

    // ---- Task intents --------------------------------------------------------------------------

    public void Rename(string id, string content)
    {
        _engine.UpdateItem(id, new JsonObject { ["content"] = content });
        Publish();
    }

    public void SetPriority(string id, Priority priority)
    {
        _engine.UpdateItem(id, new JsonObject { ["priority"] = PriorityMap.ToApi(priority) });
        Publish();
    }

    /// <summary>Sets or, with a null date, clears the due date.</summary>
    public void SetDue(string id, DateOnly? date, TimeOnly? time = null)
    {
        _engine.UpdateItem(id, new JsonObject { ["due"] = ItemFields.Due(date, time) });
        Publish();
    }

    public void Complete(string id)
    {
        _engine.CompleteItem(id);
        Publish();
    }

    public void Delete(string id)
    {
        _engine.DeleteItem(id);
        Publish();
    }

    public bool Undo()
    {
        var undone = _engine.Undo();
        if (undone)
            Publish();
        return undone;
    }

    /// <summary>Moves a task one place up or down among its siblings.</summary>
    /// <returns>False when it was already at that end, so the caller can skip a needless sync.</returns>
    public bool Move(string id, int offset)
    {
        var moved = _engine.MoveItem(id, offset);
        if (moved)
            Publish();
        return moved;
    }

    /// <summary>Makes a task a child of the one above it.</summary>
    public bool Indent(string id)
    {
        var indented = _engine.IndentItem(id);
        if (indented)
            Publish();
        return indented;
    }

    /// <summary>Promotes a sub-task alongside its parent.</summary>
    public bool Outdent(string id)
    {
        var outdented = _engine.OutdentItem(id);
        if (outdented)
            Publish();
        return outdented;
    }

    // ---- Label intents -------------------------------------------------------------------------

    /// <summary>The label of this name, if the account has one. Sidebar rows carry names, not ids.</summary>
    public Label? LabelNamed(string name)
        => Labels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces the labels on a task with the given set.</summary>
    public void SetLabels(string id, IReadOnlyList<string> labels)
    {
        _engine.SetItemLabels(id, labels);
        Publish();
    }

    /// <summary>Adds a label to the account if it isn't there already, and returns its name.</summary>
    public string AddLabel(string name)
    {
        var trimmed = name.Trim();
        var existing = _engine.Snapshot().Labels
            .FirstOrDefault(l => string.Equals(l.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
            return existing.Name;

        _engine.AddLabel(trimmed);
        Publish();
        return trimmed;
    }

    public void RenameLabel(string id, string name)
    {
        _engine.RenameLabel(id, name);
        Publish();
    }

    public void ToggleLabelFavorite(string id)
    {
        if (_engine.Snapshot().Labels.FirstOrDefault(l => l.Id == id) is not { } label)
            return;

        _engine.SetLabelFavorite(id, !label.IsFavorite);
        Publish();
    }

    public void DeleteLabel(string id)
    {
        var name = _engine.Snapshot().Labels.FirstOrDefault(l => l.Id == id)?.Name;

        _engine.DeleteLabel(id);

        // Don't leave the outline showing a label that no longer exists.
        if (name is not null && string.Equals(Selection.LabelName, name, StringComparison.OrdinalIgnoreCase))
            Selection = ViewSelection.Default;

        Publish();
    }

    // ---- Structure intents ---------------------------------------------------------------------

    public void AddProject(string name)
    {
        _engine.AddProject(name);
        Publish();
    }

    public void RenameProject(string id, string name)
    {
        _engine.RenameProject(id, name);
        Publish();
    }

    public void ToggleProjectFavorite(string id)
    {
        var current = _engine.Snapshot().Projects.FirstOrDefault(p => p.Id == id);
        if (current is null)
            return;

        _engine.SetProjectFavorite(id, !current.IsFavorite);
        Publish();
    }

    public void DeleteProject(string id)
    {
        // Captured first: the delete cascades to descendants, so afterwards there's no way to tell
        // whether the selected section or project belonged to what just went.
        var doomed = DescendantProjects(id);
        var doomedSections = _engine.Snapshot().Sections
            .Where(s => s.ProjectId is { } p && doomed.Contains(p))
            .Select(s => s.Id)
            .ToHashSet();

        _engine.DeleteProject(id);

        // Don't leave the outline pointed at something that no longer exists.
        if ((Selection.ProjectId is { } selected && doomed.Contains(selected))
            || (Selection.SectionId is { } section && doomedSections.Contains(section)))
        {
            Selection = ViewSelection.Default;
        }

        Publish();
    }

    private HashSet<string> DescendantProjects(string id)
    {
        var projects = _engine.Snapshot().Projects;
        var doomed = new HashSet<string> { id };
        bool grew;
        do
        {
            grew = false;
            foreach (var project in projects)
                if (project.ParentId is { } parent && doomed.Contains(parent) && doomed.Add(project.Id))
                    grew = true;
        }
        while (grew);
        return doomed;
    }

    public void AddSection(string name, string projectId)
    {
        _engine.AddSection(name, projectId);
        Publish();
    }

    public void RenameSection(string id, string name)
    {
        _engine.RenameSection(id, name);
        Publish();
    }

    public void DeleteSection(string id)
    {
        _engine.DeleteSection(id);

        if (Selection.SectionId == id)
            Selection = ViewSelection.Default;

        Publish();
    }

    // ---- Building the view ---------------------------------------------------------------------

    private string? SectionProjectId()
        => Selection.SectionId is { } id
            ? _engine.Snapshot().Sections.FirstOrDefault(s => s.Id == id)?.ProjectId
            : null;

    private (QuickAddParse Parse, string? ProjectId, string? SectionId) Resolve(string text)
    {
        var parse = _parser.Parse(text);

        // Every word was a token, so there is no task text left. Keep the raw input rather than
        // creating a blank task the server would reject and silently discard.
        if (string.IsNullOrWhiteSpace(parse.Content))
            parse = parse with { Content = text.Trim() };

        // An ambiguous name is treated as unresolved: filing the task under whichever project
        // happened to be enumerated first would be a guess.
        string? projectId = null;
        if (parse.ProjectName is not null)
        {
            var projects = _engine.FindProjectsByName(parse.ProjectName);
            if (projects.Count == 1)
                projectId = projects[0].Id;
        }

        string? sectionId = null;

        // A named project that didn't resolve means we don't know where this task belongs, so a
        // section matching by name alone would file it somewhere the user never asked for.
        var projectKnown = parse.ProjectName is null || projectId is not null;
        if (parse.SectionName is not null && projectKnown)
        {
            var sections = _engine.FindSectionsByName(parse.SectionName, projectId);
            if (sections.Count == 1)
            {
                // A bare section implies its project; sending one without the other is invalid, so
                // a section that doesn't name its own project is no use to us.
                if (projectId is not null)
                    sectionId = sections[0].Id;
                else if (sections[0].ProjectId is { } owner)
                {
                    sectionId = sections[0].Id;
                    projectId = owner;
                }
            }
        }

        return (parse, projectId, sectionId);
    }

    /// <summary>Re-reads the model and republishes. Call after anything that mutates the engine.</summary>
    private void Publish()
    {
        lock (_publishing)
        {
            var snapshot = _engine.Snapshot();

            // Cleared before the outline is built, which is what decides whether it gets set again.
            UnsupportedFilter = null;

            Labels = snapshot.Labels.OrderBy(l => l.ItemOrder).ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Sidebar = BuildSidebar(snapshot);
            _allRows = BuildOutline(snapshot, scoped: true);

            // Search runs over everything loaded, not just the view in front of you — otherwise
            // Ctrl+F from Today silently misses most of the account.
            _searchableRows = BuildOutline(snapshot, scoped: false);

            ApplyFilter(snapshot.PendingCount, snapshot.FailedCount);
        }

        RowsChanged?.Invoke();
    }

    /// <summary>Re-applies the search filter to the rows already projected.</summary>
    private void Republish()
    {
        lock (_publishing)
            ApplyFilter(_engine.PendingCount, _engine.FailedCount);

        RowsChanged?.Invoke();
    }

    private List<SidebarNode> BuildSidebar(ModelSnapshot snapshot)
    {
        var today = snapshot.Today;
        var inbox = snapshot.InboxProjectId;
        var active = VisibleItems(snapshot);

        // Archived projects and sections are still returned by sync; they don't belong in the sidebar.
        var projects = snapshot.Projects.Where(p => !p.IsArchived && p.Id.Length > 0).ToList();
        var sections = snapshot.Sections.Where(s => !s.IsArchived && s.Id.Length > 0).ToList();

        // Every count in the sidebar comes off one pass. Counting per node instead would be a scan
        // of the whole account per project and per section, on every publish — and a sync publishes.
        var todayCount = 0;
        var upcomingCount = 0;
        var inboxCount = 0;
        var byProject = new Dictionary<string, int>(StringComparer.Ordinal);
        var bySection = new Dictionary<string, int>(StringComparer.Ordinal);
        var byLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in active)
        {
            if (SmartViews.IsToday(item, today, snapshot.TimeZone)) todayCount++;
            if (SmartViews.IsUpcoming(item, today, snapshot.TimeZone)) upcomingCount++;
            if (SmartViews.IsInbox(item, inbox)) inboxCount++;

            if (item.ProjectId is { } itemProject)
                byProject[itemProject] = byProject.GetValueOrDefault(itemProject) + 1;
            if (item.SectionId is { } itemSection)
                bySection[itemSection] = bySection.GetValueOrDefault(itemSection) + 1;

            foreach (var label in item.Labels)
                byLabel[label] = byLabel.GetValueOrDefault(label) + 1;
        }

        var nodes = new List<SidebarNode>
        {
            View(SmartView.Today, "Today", todayCount),
            View(SmartView.Upcoming, "Upcoming", upcomingCount),
            View(SmartView.Inbox, "Inbox", inboxCount),
        };

        var labels = snapshot.Labels
            .Where(l => l.Id.Length > 0)
            .OrderBy(l => l.ItemOrder)
            .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var filters = snapshot.Filters
            .Where(f => f.Id.Length > 0)
            .OrderBy(f => f.ItemOrder)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Favourites are grouped by kind — projects, then labels, then filters — each in its own
        // order. Interleaving them would put three unrelated order fields in one sequence.
        var favouriteProjects = projects
            .Where(p => p.IsFavorite)
            .OrderBy(p => p.ChildOrder)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var favouriteLabels = labels.Where(l => l.IsFavorite).ToList();
        var favouriteFilters = filters.Where(f => f.IsFavorite).ToList();

        if (favouriteProjects.Count + favouriteLabels.Count + favouriteFilters.Count > 0)
        {
            nodes.Add(Header("Favourites"));

            // Keyed apart from their copies further down, so clicking one doesn't select the other.
            foreach (var favorite in favouriteProjects)
                nodes.Add(new SidebarNode(SidebarKind.Project, favorite.Id, favorite.Name, 1,
                    Key: SidebarKeys.Favourite(SidebarKind.Project, favorite.Id), IsFavorite: true, Count: byProject.GetValueOrDefault(favorite.Id)));

            foreach (var favorite in favouriteLabels)
                nodes.Add(new SidebarNode(SidebarKind.Label, favorite.Name, favorite.Name, 1,
                    Key: SidebarKeys.Favourite(SidebarKind.Label, favorite.Name), IsFavorite: true, Count: byLabel.GetValueOrDefault(favorite.Name)));

            foreach (var favorite in favouriteFilters)
                nodes.Add(new SidebarNode(SidebarKind.Filter, favorite.Id, favorite.Name, 1,
                    Key: SidebarKeys.Favourite(SidebarKind.Filter, favorite.Id), IsFavorite: true));
        }

        nodes.Add(Header("Projects"));

        // Projects nest, and each carries its sections beneath it.
        var byParent = projects
            .GroupBy(p => p.ParentId ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.ChildOrder).ThenBy(p => p.Id, StringComparer.Ordinal).ToList());

        var listed = new HashSet<string>();
        AddProjects(string.Empty, 1);

        if (labels.Count > 0)
        {
            nodes.Add(Header("Labels"));

            // Selected by name, since that is how a task refers to a label. Two labels sharing a
            // name would be the same view, so they are listed once.
            foreach (var label in labels.DistinctBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
                nodes.Add(new SidebarNode(SidebarKind.Label, label.Name, label.Name, 1,
                    Key: SidebarKeys.For(SidebarKind.Label, label.Name), IsFavorite: label.IsFavorite, Count: byLabel.GetValueOrDefault(label.Name)));
        }

        if (filters.Count > 0)
        {
            nodes.Add(Header("Filters"));

            // No count: unlike the others it can't be had from the single pass above, and running
            // every saved query over every task on each publish is not worth a number in brackets.
            foreach (var filter in filters)
                nodes.Add(new SidebarNode(SidebarKind.Filter, filter.Id, filter.Name, 1,
                    Key: SidebarKeys.For(SidebarKind.Filter, filter.Id), IsFavorite: filter.IsFavorite));
        }

        return nodes;

        void AddProjects(string parentKey, int depth)
        {
            if (!byParent.TryGetValue(parentKey, out var children))
                return;

            foreach (var project in children)
            {
                // A parent cycle in the data would otherwise recurse until the stack goes.
                if (!listed.Add(project.Id))
                    continue;

                nodes.Add(new SidebarNode(SidebarKind.Project, project.Id, project.Name, depth,
                    Key: SidebarKeys.For(SidebarKind.Project, project.Id), IsFavorite: project.IsFavorite, Count: byProject.GetValueOrDefault(project.Id)));

                var owned = sections
                    .Where(s => s.ProjectId == project.Id)
                    .OrderBy(s => s.SectionOrder)
                    .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase);

                foreach (var section in owned)
                    nodes.Add(new SidebarNode(SidebarKind.Section, section.Id, section.Name, depth + 1,
                        Key: SidebarKeys.For(SidebarKind.Section, section.Id), Count: bySection.GetValueOrDefault(section.Id)));

                AddProjects(project.Id, depth + 1);
            }
        }

        SidebarNode View(SmartView view, string label, int count)
            => new(SidebarKind.SmartView, view.ToString(), label, 0, Key: SidebarKeys.For(SidebarKind.SmartView, view.ToString()), View: view, Count: count);

        SidebarNode Header(string label)
            => new(SidebarKind.Header, label, label, 0, Key: SidebarKeys.For(SidebarKind.Header, label));
    }

    /// <summary>Active tasks that aren't filed under an archived project.</summary>
    private static List<TaskItem> VisibleItems(ModelSnapshot snapshot)
    {
        var archived = snapshot.Projects.Where(p => p.IsArchived).Select(p => p.Id).ToHashSet();
        return snapshot.Items
            .Where(i => !i.Completed && (i.ProjectId is null || !archived.Contains(i.ProjectId)))
            .ToList();
    }

    /// <summary>
    /// Flattens the tasks in the current selection into an outline, depth-first, so a sub-task
    /// appears under its parent. A task whose parent isn't in view becomes a root of its own.
    /// </summary>
    private List<TaskRow> BuildOutline(ModelSnapshot snapshot, bool scoped)
    {
        var projects = snapshot.Projects.DistinctBy(p => p.Id).ToDictionary(p => p.Id, p => p.Name);
        var selected = scoped ? InSelection(snapshot) : _ => true;
        var visible = VisibleItems(snapshot)
            .Where(i => i.Id.Length > 0 && selected(i))
            .ToList();
        var present = visible.Select(i => i.Id).ToHashSet();

        var byParent = visible
            .GroupBy(i => i.ParentId is { } p && present.Contains(p) ? p : string.Empty)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(i => i.ProjectId ?? string.Empty, StringComparer.Ordinal)
                      .ThenBy(i => i.ChildOrder)
                      .ThenBy(i => i.Id, StringComparer.Ordinal)
                      .ToList());

        var rows = new List<TaskRow>(visible.Count);
        var emitted = new HashSet<string>();
        Emit(string.Empty, 0);
        return rows;

        void Emit(string parentKey, int depth)
        {
            if (!byParent.TryGetValue(parentKey, out var children))
                return;

            foreach (var item in children)
            {
                // A parent cycle in the data would otherwise recurse until the stack goes.
                if (!emitted.Add(item.Id))
                    continue;

                rows.Add(Row(item, depth));
                Emit(item.Id, depth + 1);
            }
        }

        TaskRow Row(TaskItem item, int depth) => new(
            item.Id,
            item.Content,
            item.Priority,
            item.ProjectId is not null && projects.TryGetValue(item.ProjectId, out var name) ? name : string.Empty,
            item.DueText ?? item.DueDate ?? string.Empty,
            item.Labels,
            depth);
    }

    /// <summary>
    /// The test for "is this task in the current view", resolved once per publish. A filter has to
    /// be parsed and its projects indexed, which is far too much work to repeat per task.
    /// </summary>
    private Func<TaskItem, bool> InSelection(ModelSnapshot snapshot)
    {
        if (Selection.SectionId is { } sectionId)
            return item => item.SectionId == sectionId;

        if (Selection.ProjectId is { } projectId)
            return item => item.ProjectId == projectId;

        if (Selection.LabelName is { } label)
            return item => item.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);

        if (Selection.FilterId is { } filterId)
            return FilterPredicate(snapshot, filterId);

        return Selection.View is not { } view
            ? _ => true
            : item => SmartViews.Matches(item, view, snapshot.Today, snapshot.TimeZone, snapshot.InboxProjectId);
    }

    private Func<TaskItem, bool> FilterPredicate(ModelSnapshot snapshot, string filterId)
    {
        var filter = snapshot.Filters.FirstOrDefault(f => f.Id == filterId);
        if (filter is null)
            return _ => false;

        var vocabulary = FilterVocabulary.From(snapshot.Projects, snapshot.Labels);
        var parsed = FilterParser.Parse(filter.Query, vocabulary);

        if (!parsed.IsSupported)
        {
            // Nothing, not everything. A full task list looks like a filter that ran and matched
            // broadly, which is the mistake this whole path exists to avoid.
            UnsupportedFilter = filter.Query;
            return _ => false;
        }

        var context = new FilterContext(snapshot.Projects, snapshot.Today, snapshot.TimeZone);
        return item => FilterEvaluator.Matches(parsed.Expression!, item, context);
    }

    private void ApplyFilter(int pending, int failed)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            Rows = _allRows;
        }
        else
        {
            // Matches rarely share a parent, so a filtered result is a flat list rather than a
            // tree with most of its structure missing.
            var q = SearchQuery.Trim();
            Rows = _searchableRows
                .Where(r =>
                    r.Content.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    r.Project.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    r.Labels.Any(l => l.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r with { Depth = 0 })
                .ToList();
        }

        Status = string.Join(" · ",
            new[]
            {
                Rows.Count == 1 ? "1 task" : $"{Rows.Count} tasks",
                IsOffline ? "offline (showing cached)" : null,
                pending > 0 ? $"{pending} pending" : null,
                failed > 0 ? $"{failed} failed" : null,
            }.Where(s => s is not null));
    }
}
