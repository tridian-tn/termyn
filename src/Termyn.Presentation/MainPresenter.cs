using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;

namespace Termyn.Presentation;

/// <summary>A single row rendered in the task list.</summary>
public sealed record TaskRow(string Id, string Content, Priority Priority, string Project, string Due);

/// <summary>
/// Drives the task list: exposes the engine's model as rows and turns user intents into engine
/// operations. UI-framework agnostic, so another platform's view can bind to the same presenter.
/// </summary>
public sealed class MainPresenter
{
    private readonly SyncEngine _engine;
    private readonly QuickAddParser _parser;

    public MainPresenter(SyncEngine engine, QuickAddParser parser)
    {
        _engine = engine;
        _parser = parser;
        Publish(); // reflect whatever the engine already has loaded
    }

    /// <summary>Raised whenever <see cref="Rows"/> and <see cref="Status"/> have been refreshed.</summary>
    public event Action? RowsChanged;

    public IReadOnlyList<TaskRow> Rows { get; private set; } = [];

    public string Status { get; private set; } = "Loading…";

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

        try
        {
            await _engine.SyncAsync(ct);
        }
        catch (TodoistNetworkException)
        {
            IsOffline = true;
        }

        Publish();
    }

    /// <summary>Reconciles with the server and republishes, keeping the current view if offline.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
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
    }

    /// <summary>
    /// Captures a task from quick-add text. Online, the server parses it so the result matches the
    /// web app; offline, the bounded local grammar is used and the task syncs later.
    /// </summary>
    public async Task CaptureAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var captured = false;
        try
        {
            captured = await _engine.QuickAddOnlineAsync(text, ct);
        }
        catch (TodoistNetworkException)
        {
            captured = false;
        }

        if (!captured)
        {
            IsOffline = true;
            _engine.AddItem(ItemFields.ForAdd(_parser.Parse(text), ResolveProjectId));
        }

        Publish();
    }

    /// <summary>Shows what the local parser makes of the text, for the capture preview.</summary>
    public QuickAddParse Preview(string text) => _parser.Parse(text);

    public void Search(string query)
    {
        SearchQuery = query ?? string.Empty;
        Publish();
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

    /// <summary>Moves a task one place up or down among the currently visible rows.</summary>
    public void Move(string id, int offset)
    {
        var ids = Rows.Select(r => r.Id).ToList();
        var from = ids.IndexOf(id);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= ids.Count)
            return;

        ids.RemoveAt(from);
        ids.Insert(to, id);
        _engine.ReorderItems(ids);
        Publish();
    }

    private string? ResolveProjectId(string name)
        => _engine.Model.Projects()
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;

    private void Publish()
    {
        var projects = _engine.Model.Projects()
            .DistinctBy(p => p.Id)
            .ToDictionary(p => p.Id, p => p.Name);

        var rows = _engine.Model.Items()
            .Where(i => !i.Completed)
            .OrderBy(i => i.ChildOrder)
            .Select(i => new TaskRow(
                i.Id,
                i.Content,
                i.Priority,
                i.ProjectId is not null && projects.TryGetValue(i.ProjectId, out var name) ? name : string.Empty,
                i.DueText ?? i.DueDate ?? string.Empty));

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            rows = rows.Where(r =>
                r.Content.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Project.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        Rows = rows.ToList();

        var pending = _engine.PendingCount;
        Status = string.Join(" · ",
            new[]
            {
                $"{Rows.Count} tasks",
                IsOffline ? "offline (showing cached)" : null,
                pending > 0 ? $"{pending} pending" : null,
                _engine.FailedCount > 0 ? $"{_engine.FailedCount} failed" : null,
            }.Where(s => s is not null));

        RowsChanged?.Invoke();
    }
}
