using System.Text;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// An origin's response body with a mutation applied to it as it arrives, for the bodies that
/// must not be buffered.
///
/// It is a <see cref="Stream"/> rather than something the transformer copies through, because
/// that is what lets the forwarder keep doing exactly what it already does: it reads a chunk,
/// writes it, and flushes, and an event stream reaches the client one event at a time. Anything
/// that collected the body first would turn a live stream into a response that arrives when the
/// conversation ends.
///
/// Three things it must get right, and each of them has cost a hang or a truncated response
/// somewhere before:
///
/// A read never returns zero until the stream is really over. Zero is end-of-stream to every
/// caller, and a chunk that the mutation holds back in full -- which is what happens when a
/// packet boundary lands inside a value being restored -- produces no output at all. That reads
/// again rather than returning.
///
/// Characters are decoded across chunk boundaries, not within them. A UTF-8 sequence split
/// between two packets is ordinary on a real connection, and decoding each packet on its own
/// turns the split character into two replacement characters that then differ from the text the
/// mutation is looking for.
///
/// The origin stream is this one's to close. The content it replaced is deliberately not
/// disposed -- disposing it would close this stream's source out from under it -- so the pooled
/// connection is released when this is.
/// </summary>
sealed class RestoringStream : Stream
{
    /// <summary>Read from the origin at a time. An event frame is a few hundred bytes; this is
    /// large enough that a burst is not read a frame at a time and small enough that a slow
    /// conversation is not waiting for it to fill -- which it never does, because a read
    /// returns what has arrived.</summary>
    private const int ChunkBytes = 16 * 1024;

    private readonly Stream _origin;

    private readonly IExchangeStreamMutation _mutation;

    private readonly Encoding _encoding;

    private readonly Decoder _decoder;

    private readonly byte[] _read = new byte[ChunkBytes];

    private readonly char[] _decoded;

    private byte[] _pending = [];

    private int _pendingOffset;

    private bool _originEnded;

    private bool _flushed;

    public RestoringStream(Stream origin, IExchangeStreamMutation mutation, Encoding encoding)
    {
        _origin = origin;
        _mutation = mutation;
        _encoding = encoding;
        _decoder = encoding.GetDecoder();
        _decoded = new char[encoding.GetMaxCharCount(ChunkBytes)];
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_pendingOffset < _pending.Length)
            {
                int taken = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
                _pending.AsMemory(_pendingOffset, taken).CopyTo(buffer);
                _pendingOffset += taken;

                return taken;
            }

            if (_flushed)
                return 0;

            if (_originEnded)
            {
                // The decoder may still be holding the tail of a character, and the mutation the
                // tail of a value. The first goes through the mutation like every other chunk
                // before the second asks it for what is left.
                Hold(_mutation.Mutate(Decode([], flush: true)) + _mutation.Flush());
                _flushed = true;

                continue;
            }

            int received = await _origin.ReadAsync(_read, cancellationToken).ConfigureAwait(false);

            if (received == 0)
            {
                _originEnded = true;

                continue;
            }

            Hold(_mutation.Mutate(Decode(_read.AsSpan(0, received), flush: false)));
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await _origin.DisposeAsync().ConfigureAwait(false);

        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _origin.Dispose();

        base.Dispose(disposing);
    }

    private string Decode(ReadOnlySpan<byte> bytes, bool flush)
    {
        int characters = _decoder.GetChars(bytes, _decoded, flush);

        return characters == 0 ? string.Empty : new string(_decoded, 0, characters);
    }

    /// <summary>Whatever the mutation produced, as the bytes the client will be handed. Empty is
    /// the ordinary case for a chunk that was held back, and leaves nothing to give out.</summary>
    private void Hold(string text)
    {
        _pending = text.Length == 0 ? [] : _encoding.GetBytes(text);
        _pendingOffset = 0;
    }
}
