using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Termyn.Core.Platform;

namespace Termyn.Platform.Windows;

/// <summary>
/// One Termyn per user, held by a named mutex, with a named pipe so a second launch can hand over
/// what it was asked to do before it exits.
/// </summary>
/// <remarks>
/// Both names are scoped to the user, not the machine: two people signed into the same box each get
/// their own instance, since they have their own cache, outbox and token.
/// </remarks>
public sealed class WindowsSingleInstance : ISingleInstance
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopping = new();

    private Mutex? _held;
    private bool _disposed;

    public WindowsSingleInstance(string? scope = null)
    {
        // Hashed rather than used raw: a user name can carry characters a pipe name may not, and the
        // Local\ prefix keeps the mutex inside this logon session.
        var user = scope ?? Environment.UserName;
        var tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16];
        _mutexName = $@"Local\Termyn-{tag}";
        _pipeName = $"Termyn-{tag}";
    }

    public event Action<string>? SignalReceived;

    /// <inheritdoc />
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_held is not null)
            return true;

        var mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }

        _held = mutex;
        _ = Task.Run(() => ListenAsync(_stopping.Token));
        return true;
    }

    /// <inheritdoc />
    public bool TrySignal(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);

            // Short: the holder is on the same machine and already running, so a wait of any length
            // means it is wedged — and this process is about to exit either way.
            client.Connect(TimeSpan.FromSeconds(2));

            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(message);
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
                using var nudge = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
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

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, new UTF8Encoding(false));
                if (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { Length: > 0 } message)
                    SignalReceived?.Invoke(message.Trim());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One bad connection must not stop us listening for the next.
            }
        }
    }
}
