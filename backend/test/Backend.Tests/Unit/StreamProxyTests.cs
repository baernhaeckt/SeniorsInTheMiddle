using System.Buffers;
using System.IO.Pipelines;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// How an opaque tunnel decides it is over.
///
/// The interesting case is the asymmetric one: the two directions of a tunnel end
/// independently, and treating either one's end of stream as the end of the whole thing
/// truncates the reply a half-closed client is still waiting for.
/// </summary>
[TestClass]
public class StreamProxyTests
{
    /// <summary>
    /// A client that half-closes its send side -- SMTP after QUIT, an upload framed by close --
    /// has finished asking, not finished listening. Its direction reaching end of stream must
    /// not cancel the origin's, or the reply arrives cut off and nothing anywhere says so.
    /// </summary>
    [TestMethod]
    public async Task A_Client_That_Half_Closes_Still_Receives_The_Whole_Reply()
    {
        const string reply = "220 mail.example.com ESMTP ready\r\n";

        Pipe toProxy = new();
        Pipe fromProxy = new();

        // The client said everything it was going to say and shut its send side.
        await toProxy.Writer.CompleteAsync();

        // The origin takes a moment before it answers, which is the window in which a
        // premature cancellation would land.
        SlowReplyStream remote = new(Encoding.ASCII.GetBytes(reply), TimeSpan.FromMilliseconds(250));

        StreamProxy proxy = new(
            new DuplexPipe(toProxy.Reader, fromProxy.Writer),
            remote,
            NullLogger<StreamProxy>.Instance);

        // The bound is the assertion as much as the content is: a tunnel that waits on a
        // direction nobody will ever close is as broken as one that truncates.
        await proxy.ProxyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await fromProxy.Writer.CompleteAsync();

        ReadResult delivered = await fromProxy.Reader.ReadAtLeastAsync(reply.Length);

        Assert.AreEqual(reply, Encoding.ASCII.GetString(BuffersExtensions.ToArray(delivered.Buffer)));
    }

    /// <summary>Pairs an unrelated reader and writer into one pipe, so the two directions of
    /// the proxied connection can be driven independently.</summary>
    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }

    /// <summary>Answers <paramref name="reply"/> after <paramref name="delay"/>, then ends.</summary>
    private sealed class SlowReplyStream(byte[] reply, TimeSpan delay) : Stream
    {
        private bool _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_sent)
                return 0;

            await Task.Delay(delay, cancellationToken);
            _sent = true;
            reply.CopyTo(buffer);

            return reply.Length;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
