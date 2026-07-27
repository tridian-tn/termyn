namespace Termyn.Core.Platform;

/// <summary>Supplies today's date, so date-sensitive logic can be tested deterministically.</summary>
public interface IClock
{
    DateOnly Today { get; }
}

/// <summary>The real clock, reading the machine's local date.</summary>
public sealed class SystemClock : IClock
{
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
}
