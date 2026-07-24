using Termyn.Core.Model;

namespace Termyn.Core.Api;

/// <summary>The materialised result of a sync read.</summary>
public sealed class SyncResult
{
    public required string SyncToken { get; init; }
    public bool FullSync { get; init; }
    public IReadOnlyList<TaskItem> Items { get; init; } = [];
    public IReadOnlyList<Project> Projects { get; init; } = [];
}
