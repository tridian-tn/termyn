using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;

namespace Termyn.Presentation;

/// <summary>A single row rendered in the task list.</summary>
public sealed record TaskRow(string Id, string Content, Priority Priority, string Project, string Due, IReadOnlyList<string> Labels);

/// <summary>What the local parser made of some capture text, and whether its names resolved.</summary>
public sealed record CapturePreview(QuickAddParse Parse, bool ProjectResolved, bool SectionResolved);

/// <summary>
/// Drives the task list: exposes the engine's model as rows and turns user intents into engine
/// operations. UI-framework agnostic, so another platform's view can bind to the same presenter.
/// </summary>
public sealed class MainPresenter
{
    private readonly SyncEngine _engine;
    private readonly QuickAddParser _parser;
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
        return _engine.PendingCount > 0 && !IsOffline;
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
            var parse = _parser.Parse(text);

            // Every word was a token, so there is no task text left. Keep the raw input rather than
            // creating a blank task the server would reject and silently discard.
            var content = string.IsNullOrWhiteSpace(parse.Content) ? text.Trim() : parse.Content;
            var resolved = parse with { Content = content };

            var projectId = ResolveProjectId(parse.ProjectName);
            _engine.AddItem(ItemFields.ForAdd(resolved, projectId, ResolveSectionId(parse.SectionName, projectId)));
        }

        Publish();
    }

    /// <summary>Shows what the local parser understood, for the capture preview.</summary>
    public CapturePreview Preview(string text)
    {
        var parse = _parser.Parse(text);
        var projectId = ResolveProjectId(parse.ProjectName);
        return new CapturePreview(
            parse,
            parse.ProjectName is null || projectId is not null,
            parse.SectionName is null || ResolveSectionId(parse.SectionName, projectId) is not null);
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

    public void Reopen(string id)
    {
        _engine.ReopenItem(id);
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
    public void Move(string id, int offset)
    {
        _engine.MoveItem(id, offset);
        Publish();
    }

    private string? ResolveProjectId(string? name)
        => name is null
            ? null
            : _engine.Snapshot().Projects
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>
    /// Resolves a section name within its project. Section names are only unique inside a project,
    /// so without one an ambiguous name resolves to nothing rather than filing the task under an
    /// unrelated project's section.
    /// </summary>
    private string? ResolveSectionId(string? name, string? projectId)
    {
        if (name is null)
            return null;

        var matches = _engine.Snapshot().Sections
            .Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            .Where(s => projectId is null || s.ProjectId == projectId)
            .ToList();

        return matches.Count == 1 ? matches[0].Id : null;
    }

    /// <summary>Re-reads the model and republishes. Call after anything that mutates the engine.</summary>
    private void Publish()
    {
        var snapshot = _engine.Snapshot();
        var projects = snapshot.Projects
            .DistinctBy(p => p.Id)
            .ToDictionary(p => p.Id, p => p.Name);

        _allRows = snapshot.Items
            .Where(i => !i.Completed)
            .OrderBy(i => i.ChildOrder)
            .Select(i => new TaskRow(
                i.Id,
                i.Content,
                i.Priority,
                i.ProjectId is not null && projects.TryGetValue(i.ProjectId, out var name) ? name : string.Empty,
                i.DueText ?? i.DueDate ?? string.Empty,
                i.Labels))
            .ToList();

        Republish(snapshot);
    }

    /// <summary>Re-applies the search filter to the rows already projected.</summary>
    private void Republish(ModelSnapshot? snapshot = null)
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

        var pending = snapshot?.PendingCount ?? _engine.PendingCount;
        var failed = snapshot?.FailedCount ?? _engine.FailedCount;
        Status = string.Join(" · ",
            new[]
            {
                Rows.Count == 1 ? "1 task" : $"{Rows.Count} tasks",
                IsOffline ? "offline (showing cached)" : null,
                pending > 0 ? $"{pending} pending" : null,
                failed > 0 ? $"{failed} failed" : null,
            }.Where(s => s is not null));

        RowsChanged?.Invoke();
    }
}
