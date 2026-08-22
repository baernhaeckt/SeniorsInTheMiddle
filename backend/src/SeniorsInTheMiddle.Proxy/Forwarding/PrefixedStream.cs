namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// The bytes already read from a stream, followed by whatever is still in it.
///
/// A request body cannot be rewound, so a body that turns out to be too large to rewrite has
/// to be served from the part that was read while measuring it, and then from the socket. It
/// reads forward only, like the stream it stands in for.
///
/// It deliberately does not dispose <c>rest</c>: the request body belongs to the server, and
/// closing it here would end the connection under Kestrel's feet.
/// </summary>
sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream
{
    private int consumed;

    /// <summary>
    /// Whether the prefix still has bytes to give.
    ///
    /// The switch is made on this rather than on how many bytes a copy produced. A caller
    /// reading into an empty buffer -- the "wait until data arrives" idiom Kestrel's body
    /// stream supports -- would otherwise be sent to the socket while buffered bytes sat
    /// unread, and wait for a client that already sent them.
    /// </summary>
    private bool PrefixRemains => consumed < prefix.Length;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => PrefixRemains
            ? CopyFromPrefix(buffer.AsSpan(offset, count))
            : rest.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
        => PrefixRemains
            ? ValueTask.FromResult(CopyFromPrefix(buffer.Span))
            : rest.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Serves as much of the prefix as fits in <paramref name="destination"/>.</summary>
    private int CopyFromPrefix(Span<byte> destination)
    {
        int available = Math.Min(prefix.Length - consumed, destination.Length);
        prefix.AsSpan(consumed, available).CopyTo(destination);
        consumed += available;

        return available;
    }
}
