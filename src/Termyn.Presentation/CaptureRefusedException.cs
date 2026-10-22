namespace Termyn.Presentation;

/// <summary>
/// A capture the app won't make, with a reason the box can show: text the local grammar reads but
/// can't honour on its own, like a repeat only the server resolves.
/// </summary>
/// <remarks>
/// Carried back to the capture box, which puts the words back and says why, rather than filing a
/// task that quietly dropped what was asked for. It isn't a failure, so it isn't logged or reported
/// as one.
/// </remarks>
/// <param name="message">What to tell the user, in their own language</param>
public sealed class CaptureRefusedException(string message) : Exception(message);
