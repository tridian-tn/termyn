namespace Termyn.Core.Platform;

/// <summary>Supplies the current time, so date-sensitive logic can be tested deterministically.</summary>
public interface IClock
{
    /// <summary>The machine's local date, which is what typed dates like "tomorrow" mean.</summary>
    DateOnly Today { get; }

    /// <summary>The current instant, for working out the date in the account's own timezone.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock.</summary>
public sealed class SystemClock : IClock
{
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
