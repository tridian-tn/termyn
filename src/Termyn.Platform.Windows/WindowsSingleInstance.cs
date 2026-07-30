using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Termyn.Core.Platform;

namespace Termyn.Platform.Windows;

/// <summary>
/// One Termyn per user, held by a named mutex, with a named pipe so a second launch can hand over
/// what it was asked to do before it exits.
/// </summary>
/// <remarks>
/// Both names are derived from the user's SID rather than their name, since two principals can share
/// a name — a local and a domain account both called "alice" — and only one of them should hold the
/// instance.
/// <para>
/// The mutex is <c>Local\</c>, which is the logon session rather than the user. Creating a
/// <c>Global\</c> object needs a privilege standard users are not granted, and an instance that
/// couldn't start at all for those users would be far worse than the gap this leaves: two sessions of
/// one account on a multi-session host each get their own Termyn, against one cache.
/// </para>
/// <para>
/// The pipe namespace is machine-wide whatever we do, so the pipe is opened
/// <see cref="PipeOptions.CurrentUserOnly"/> at both ends — that ACLs the server to this user and
/// makes the client check the owner before writing, so a name squatted by another account is refused
/// rather than talked to.
/// </para>
/// </remarks>
public sealed class WindowsSingleInstance : ISingleInstance
{
    /// <summary>Consecutive listener failures before it gives up rather than retrying forever.</summary>
    private const int MaxListenFailures = 10;

    private readonly string _mutexName;
    private readonly string _pipeName;

    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// Signals that arrived before anyone was listening. The pipe server starts as soon as the
    /// instance is acquired, but the window that handles signals doesn't exist until the cache has
    /// loaded — and on a first run there is a token dialog in between. Without this, a second launch
    /// during that window is told it handed over and nothing happens.
    /// </summary>
    private readonly List<string> _buffered = [];

    private readonly TimeSpan _retryDelay;

    private Action<string>? _received;
    private Mutex? _held;
    private Task? _listener;
    private bool _disposed;

    /// <param name="scope">Who the instance belongs to; defaults to the current user's SID.</param>
    /// <param name="retryDelay">
    /// How long the listener waits after a failure. Only shortened by tests, which would otherwise
    /// take ten seconds to watch it give up.
    /// </param>
    public WindowsSingleInstance(string? scope = null, TimeSpan? retryDelay = null)
    {
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);

        var user = scope ?? CurrentUserSid();
        var tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16];

        _mutexName = $@"Local\Termyn-{tag}";
        _pipeName = $"Termyn-{tag}";
    }

    /// <inheritdoc />
    public event Action<string>? SignalReceived
    {
        add
        {
            List<string> pending;
            lock (_gate)
            {
                _received += value;
                pending = [.. _buffered];
                _buffered.Clear();
            }

            // Anything that arrived before this subscriber is delivered to it now, so a launch during
            // startup isn't lost.
            foreach (var message in pending)
                value?.Invoke(message);
        }
        remove
        {
            lock (_gate)
                _received -= value;
        }
    }

    /// <summary>The mutex's name. Internal so a test can hold the naming rules to account.</summary>
    internal string MutexName => _mutexName;

    /// <summary>The pipe's name, for the same reason.</summary>
    internal string PipeName => _pipeName;

    /// <summary>
    /// The listening loop, so a test can watch it give up rather than inferring that from CPU — which
    /// on a shared test process measures every other test as well.
    /// </summary>
    internal Task? Listener => _listener;

    /// <inheritdoc />
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_held is not null)
            return true;

        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The name exists and is another principal's. Treat it as held rather than crashing.
            return false;
        }

        _held = mutex;
        _listener = Task.Run(() => ListenAsync(_stopping.Token));
        return true;
    }

    /// <inheritdoc />
    public bool TrySignal(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Anonymous);

            // Short: the holder is on the same machine and already running, so a wait of any length
            // means it is wedged — and this process is about to exit either way.
            client.Connect(TimeSpan.FromSeconds(2));

            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };

            // One line is one signal, so a message can't be made to look like several.
            writer.WriteLine(message.ReplaceLineEndings(" "));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _stopping.Cancel();

        // Connecting to our own pipe unblocks the server that is sitting in WaitForConnection, which
        // does not observe the cancellation token once it is already waiting.
        if (_held is not null)
        {
            try
            {
                using var nudge = new NamedPipeClientStream(
                    ".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Anonymous);
                nudge.Connect(TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // The listener is already gone.
            }
        }

        _stopping.Dispose();

        if (_held is not null)
        {
            try
            {
                _held.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not held by this thread, which happens if acquisition raced with shutdown.
            }
            _held.Dispose();
            _held = null;
        }
    }

    private static string CurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? Environment.UserName;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Environment.UserName;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                failures = 0;

                using var reader = new StreamReader(server, new UTF8Encoding(false));
                if (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { Length: > 0 } message)
                    Deliver(message.Trim());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The name can be permanently unavailable — squatted by another account, or held by
                // a stale process. Retrying flat out burned a whole core for the life of the process,
                // silently, so back off and eventually stop: signalling is a convenience, and losing
                // it must not cost the machine a core.
                if (++failures >= MaxListenFailures)
                    return;

                try
                {
                    await Task.Delay(_retryDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Hands a signal to whoever is listening, or holds it until someone is.</summary>
    private void Deliver(string message)
    {
        Action<string>? subscriber;
        lock (_gate)
        {
            subscriber = _received;
            if (subscriber is null)
            {
                _buffered.Add(message);
                return;
            }
        }

        subscriber(message);
    }
}
