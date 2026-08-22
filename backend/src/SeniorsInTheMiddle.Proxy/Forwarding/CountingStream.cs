namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Passes writes straight through and counts the bytes that went past.
///
/// A proxied response is usually chunked -- the normal framing for dynamic HTML and for most
/// JSON APIs -- and a chunked response carries no Content-Length for the proxy to read. The
/// only honest size is the one taken as the body streams, so telemetry counts here rather than
/// reporting the zero that a missing header would otherwise stand in for.
/// </summary>
sealed class CountingStream(Stream inner) : Stream
{
    /// <summary>Bytes written so far. Written from the single thread draining the response.</summary>
    public long BytesWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        BytesWritten += buffer.Length;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
