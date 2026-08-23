using System.IO.Pipelines;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// One <see cref="Stream"/> face over a separate read half and write half, which is what
/// <see cref="System.Net.Security.SslStream"/> expects when the two directions of a connection
/// are not already the same object.
/// </summary>
sealed class DuplexStream(Stream input, Stream output) : Stream
{
    public override bool CanRead => input.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => output.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => output.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => input.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => output.WriteAsync(buffer, cancellationToken);
}

/// <summary>
/// The reverse adapter: an <see cref="IDuplexPipe"/> face over a stream, so a decrypted
/// <see cref="System.Net.Security.SslStream"/> can be handed to code that speaks pipes.
///
/// Both halves leave the stream open, because the stream outlives the pipes wrapping it.
/// </summary>
sealed class StreamDuplexPipe(Stream stream) : IDuplexPipe
{
    public PipeReader Input { get; } = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
    public PipeWriter Output { get; } = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
}
