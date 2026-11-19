using Termyn.Core.Api;
using Termyn.Core.Sync;

namespace Termyn.TestSupport;

/// <summary>Having the fake server take what an engine has queued.</summary>
public static class Flushing
{
    /// <summary>Sends what is queued and has the server accept all of it.</summary>
    /// <param name="engine">The engine to sync</param>
    /// <param name="api">The fake server it syncs with</param>
    public static async Task Flush(SyncEngine engine, FakeApi api)
    {
        api.Next = cmds => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = cmds.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
        };

        await engine.SyncAsync();

        if (engine.PendingCount != 0)
        {
            throw new InvalidOperationException($"{engine.PendingCount} writes were still queued after the server took them all.");
        }
    }
}
