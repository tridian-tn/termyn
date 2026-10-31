using Termyn.Core.Sync;

namespace Termyn.Core.Tests;

internal static class Dismissing
{
    /// <summary>
    /// Lets every refused create go, the way dismissing it from the list of failures does.
    /// </summary>
    /// <remarks>
    /// A refused create is kept, failed, with whatever was queued on top of it, so nothing the user
    /// typed is lost to a refusal. Letting it go is what takes it all away, and what tests of the
    /// cancelling that follows go through first.
    /// </remarks>
    public static void DismissRefusedAdds(this SyncEngine engine)
    {
        foreach (var refused in engine.Outbox.Where(c => c.State == OutboxState.Failed && c.Type.EndsWith("_add", StringComparison.Ordinal)))
            engine.Revert(refused.Uuid);
    }
}
