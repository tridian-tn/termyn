namespace Termyn.Core.Sync;

public enum OutboxState
{
    Pending,
    Failed,
}

/// <summary>A durable, ordered pending write. <see cref="ArgsJson"/> holds only the changed fields.</summary>
public sealed class OutboxCommand
{
    public long Seq { get; set; }
    public required string Uuid { get; init; }
    public required string Type { get; init; }
    public string? TempId { get; init; }
    public required string ArgsJson { get; set; }

    /// <summary>
    /// The resource's last known server state, captured before this command's optimistic mutation.
    /// Reverting restores it, so a dropped write returns to server truth rather than unwinding a diff.
    /// </summary>
    public string? PriorJson { get; init; }

    public int Attempts { get; set; }

    /// <summary>Consecutive syncs in which the server returned no verdict for this command.</summary>
    public int NoVerdictRounds { get; set; }

    public OutboxState State { get; set; } = OutboxState.Pending;
    public string? LastError { get; set; }
}
