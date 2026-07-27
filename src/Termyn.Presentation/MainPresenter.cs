using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;

namespace Termyn.Presentation;

/// <summary>A single row rendered in the read-only task list.</summary>
public sealed record TaskRow(string Content, Priority Priority, string Project, string Due);

/// <summary>Exposes the sync engine's model as a read-only, flattened active-task list.</summary>
public sealed class MainPresenter
{
    private readonly SyncEngine _engine;

    public MainPresenter(SyncEngine engine) => _engine = engine;

    /// <summary>Raised whenever <see cref="Rows"/> and <see cref="Status"/> have been refreshed.</summary>
    public event Action? RowsChanged;

    public IReadOnlyList<TaskRow> Rows { get; private set; } = [];

    public string Status { get; private set; } = "Loading…";

    public bool IsOffline { get; private set; }

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

    private void Publish()
    {
        var projects = _engine.Model.Projects()
            .DistinctBy(p => p.Id)
            .ToDictionary(p => p.Id, p => p.Name);

        Rows = _engine.Model.Items()
            .Where(i => !i.Completed)
            .OrderBy(i => i.ChildOrder)
            .Select(i => new TaskRow(
                i.Content,
                i.Priority,
                i.ProjectId is not null && projects.TryGetValue(i.ProjectId, out var name) ? name : string.Empty,
                i.DueText ?? i.DueDate ?? string.Empty))
            .ToList();

        Status = IsOffline
            ? $"{Rows.Count} tasks · offline (showing cached)"
            : $"{Rows.Count} tasks · read-only";

        RowsChanged?.Invoke();
    }
}
