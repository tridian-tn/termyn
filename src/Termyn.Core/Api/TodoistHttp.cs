namespace Termyn.Core.Api;

/// <summary>
/// Makes the <see cref="HttpClient"/> the app talks to Todoist through, so every client sets it up
/// the same way.
/// </summary>
/// <remarks>
/// Portable: <see cref="SocketsHttpHandler"/> is the default handler on Windows and macOS alike, and
/// takes the system proxy the same way the one it replaces did.
/// </remarks>
public static class TodoistHttp
{
    /// <summary>
    /// A client with no overall timeout, connections that are replaced every few minutes, and a
    /// bound on making one.
    /// </summary>
    /// <remarks>
    /// No timeout, because <see cref="HttpClient.Timeout"/> spans sending a request's body, so any
    /// upload that took longer than it failed. Every call made with this bounds itself instead
    /// (<see cref="TodoistApiClient"/>, and the update check). The connection lifetime is so a long
    /// session in the tray picks up a DNS change rather than holding one connection for weeks.
    /// </remarks>
    public static HttpClient CreateClient()
        => new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
