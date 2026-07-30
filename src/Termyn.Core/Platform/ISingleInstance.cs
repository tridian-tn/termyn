namespace Termyn.Core.Platform;

/// <summary>
/// Keeps one Termyn per user session. Two processes would share one cache and outbox, and the
/// second would fail to register the global hotkey — so a second launch hands its intent to the
/// running instance and exits.
/// </summary>
public interface ISingleInstance : IDisposable
{
    /// <summary>Raised on the running instance when another launch signals it.</summary>
    event Action<string>? SignalReceived;

    /// <summary>Claims the session for this process.</summary>
    /// <returns>False when another instance already holds it.</returns>
    bool TryAcquire();

    /// <summary>Hands <paramref name="message"/> to the instance that holds the session.</summary>
    /// <returns>False when nothing answered, which means the holder is gone or wedged.</returns>
    bool TrySignal(string message);
}

/// <summary>The things a second launch can ask the running instance to do.</summary>
public static class InstanceSignals
{
    /// <summary>Restore and focus the main window.</summary>
    public const string Show = "show";

    /// <summary>Open the quick-add box, as the global hotkey would.</summary>
    public const string QuickAdd = "quick-add";
}
