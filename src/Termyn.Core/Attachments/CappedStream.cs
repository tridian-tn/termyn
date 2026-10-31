namespace Termyn.Core.Attachments;

/// <summary>
/// A stream to write a download into that refuses to take more than it was told to expect.
/// </summary>
/// <remarks>
/// The size is enforced on what's written rather than taken from the server's headers, which are
/// the sender's word. Refused before the bytes are written, so the file never holds more than the
/// allowance, and noted on <see cref="Exceeded"/> as well as thrown: the client between this and
/// the caller may wrap the exception as any other failure to write.
/// </remarks>
internal sealed class CappedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _allowance;
    private long _written;

    /// <param name="inner">Where the bytes go; disposed with this</param>
    /// <param name="allowance">The most that may be written</param>
    public CappedStream(Stream inner, long allowance)
    {
        _inner = inner;
        _allowance = allowance;
    }

    /// <summary>Whether a write was refused for going past the allowance.</summary>
    public bool Exceeded { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Take(buffer.Length);
        _inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Take(buffer.Length);
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Counts a write against the allowance, refusing it when it would go past.</summary>
    private void Take(int count)
    {
        if (_written + count > _allowance)
        {
            Exceeded = true;
            throw new TooLargeException();
        }

        _written += count;
    }

    /// <summary>
    /// What a refused write throws. Its own type, and not an <see cref="IOException"/>, so nothing
    /// on the way out reads it as the disk being full.
    /// </summary>
    private sealed class TooLargeException : Exception
    {
        public TooLargeException()
            : base("The download ran past the size it was expected to be.")
        {
        }
    }
}
