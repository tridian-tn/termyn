using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;

namespace Termyn.Presentation;

/// <summary>A single row rendered in the task list.</summary>
public sealed record TaskRow(string Id, string Content, Priority Priority, string Project, string Due, IReadOnlyList<string> Labels);

/// <summary>What the local parser made of some capture text, and how its names resolved.</summary>
public sealed record CapturePreview(QuickAddParse Parse, bool ProjectResolved, bool SectionResolved)
{
    /// <summary>The text the task would actually be created with.</summary>
    public string Content { get; init; } = Parse.Content;
}

/// <summary>
/// Drives the task list: exposes the engine's model as rows and turns user intents into engine
/// operations. UI-framework agnostic, so another platform's view can bind to the same presenter.
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

    public MainPresenter(SyncEngine engine, QuickAddParser parser)
    {
        _engine = engine;
        _parser = parser;
        Publish(); // reflect whatever the engine already has loaded
    }

    /// <summary>Raised whenever <see cref="Rows"/> and <see cref="Status"/> have been refreshed.</summary>
    public event Action? RowsChanged;

    public IReadOnlyList<TaskRow> Rows { get; private set; } = [];

    public string Status { get; private set; } = string.Empty;

    public bool IsOffline { get; private set; }

    public bool CanUndo => _engine.CanUndo;

    /// <summary>Free-text filter applied to the rendered rows.</summary>
    public string SearchQuery { get; private set; } = string.Empty;

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

        bool captured;
        try
        {
            captured = await _engine.QuickAddOnlineAsync(text, ct);
        }
        catch (TodoistNetworkException)
        {
            captured = false;
        }

        IsOffline = !captured;

        if (!captured)
        {
            var (parse, content, projectId, sectionId) = Resolve(text);
            _engine.AddItem(ItemFields.ForAdd(parse with { Content = content }, projectId, sectionId));
        }

        Publish();
    }

    /// <summary>Shows what the local parser understood, for the capture preview.</summary>
    public CapturePreview Preview(string text)
    {
        var (parse, content, projectId, sectionId) = Resolve(text);
        return new CapturePreview(
            parse,
            parse.ProjectName is null || projectId is not null,
            parse.SectionName is null || sectionId is not null)
        {
            Content = content,
        };
    }

    public void Search(string query)
    {
        SearchQuery = query ?? string.Empty;
        Republish();
    }

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

    /// <summary>
    /// Works out what a capture would actually create: the text, and the ids its <c>#project</c> and
    /// <c>/section</c> names resolve to. Shared by the preview and the capture itself so the two
    /// can never disagree.
    /// </summary>
    private (QuickAddParse Parse, string Content, string? ProjectId, string? SectionId) Resolve(string text)
    {
        var parse = _parser.Parse(text);

        // Every word was a token, so there is no task text left. Keep the raw input rather than
        // creating a blank task the server would reject and silently discard.
        var content = string.IsNullOrWhiteSpace(parse.Content) ? text.Trim() : parse.Content;

        var projectId = parse.ProjectName is null ? null : _engine.FindProjectByName(parse.ProjectName)?.Id;

        string? sectionId = null;
        if (parse.SectionName is not null)
        {
            // A named project that didn't resolve means we don't know where this task belongs, so a
            // section matching by name alone would file it somewhere the user never asked for.
            var projectKnown = parse.ProjectName is null || projectId is not null;
            if (projectKnown)
            {
                var matches = _engine.FindSectionsByName(parse.SectionName, projectId);
                if (matches.Count == 1)
                {
                    sectionId = matches[0].Id;
                    // A bare section implies its project; sending one without the other is invalid.
                    projectId ??= matches[0].ProjectId;
                }
            }
        }

        return (parse, content, projectId, sectionId);
    }

    /// <summary>Re-reads the model and republishes. Call after anything that mutates the engine.</summary>
    private void Publish()
    {
        lock (_publishing)
        {
            var snapshot = _engine.Snapshot();
            var projects = snapshot.Projects
                .DistinctBy(p => p.Id)
                .ToDictionary(p => p.Id, p => p.Name);

            // Ordered the way the engine groups siblings, so moving a task one place on screen is
            // the same move it makes in the model.
            _allRows = snapshot.Items
                .Where(i => !i.Completed)
                .OrderBy(i => i.ProjectId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(i => i.ParentId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(i => i.ChildOrder)
                .ThenBy(i => i.Id, StringComparer.Ordinal)
                .Select(i => new TaskRow(
                    i.Id,
                    i.Content,
                    i.Priority,
                    i.ProjectId is not null && projects.TryGetValue(i.ProjectId, out var name) ? name : string.Empty,
                    i.DueText ?? i.DueDate ?? string.Empty,
                    i.Labels))
                .ToList();

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

    private void ApplyFilter(int pending, int failed)
    {
        var rows = _allRows.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            rows = rows.Where(r =>
                r.Content.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Project.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Labels.Any(l => l.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        Rows = rows.ToList();

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
