using System.Text;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// The stream that applies a rewrite to a response nobody is allowed to buffer.
///
/// Everything here is about the three ways a stream wrapper goes wrong, each of which shows up
/// as a hang or a truncated body rather than as a wrong character: a read that returns zero
/// before the stream is over, a character decoded twice because it was split across two packets,
/// and a source left open because the wrapper never claimed it.
/// </summary>
[TestClass]
public class RestoringStreamTests
{
    /// <summary>The ordinary case: what the mutation writes is what the client reads.</summary>
    [TestMethod]
    public async Task What_The_Mutation_Writes_Is_What_Comes_Out()
    {
        string read = await ReadAllAsync(
            Upper(),
            "data: eins\n\n",
            "data: zwei\n\n");

        Assert.AreEqual("DATA: EINS\n\nDATA: ZWEI\n\n", read);
    }

    /// <summary>
    /// A mutation that holds a chunk back produces nothing for it, and nothing is not the end of
    /// the stream. Returning zero there is the bug that turns a restore into a response cut off
    /// at the first awkward packet boundary.
    /// </summary>
    [TestMethod]
    public async Task A_Chunk_Held_Back_In_Full_Does_Not_End_The_Stream()
    {
        // Holds every chunk until the one that ends in "!", the way a real hold-back waits for
        // the rest of a value it can see the beginning of.
        StringBuilder held = new();
        HoldingMutation mutation = new(chunk =>
        {
            held.Append(chunk);

            if (!chunk.EndsWith('!'))
                return string.Empty;

            string all = held.ToString();
            held.Clear();

            return all;
        });

        string read = await ReadAllAsync(mutation, "eins", "zwei", "drei!");

        Assert.AreEqual("einszweidrei!", read);
    }

    /// <summary>What the mutation is still holding when the origin ends is written, not dropped.</summary>
    [TestMethod]
    public async Task What_Is_Still_Held_At_The_End_Is_Flushed()
    {
        HoldingMutation mutation = new(_ => string.Empty, flush: () => "the tail");

        Assert.AreEqual("the tail", await ReadAllAsync(mutation, "eins", "zwei"));
    }

    /// <summary>
    /// A UTF-8 character split between two packets is one character, not two replacement marks.
    /// It is ordinary on a real connection, and a mutation that then searched for a name
    /// containing that character would never find it.
    /// </summary>
    [TestMethod]
    public async Task A_Character_Split_Across_Packets_Is_Decoded_Once()
    {
        byte[] payload = Encoding.UTF8.GetBytes("Renée");

        // Between the two bytes of the first e-acute.
        RecordingMutation mutation = new();
        string read = await ReadAllAsync(mutation, payload[..4], payload[4..]);

        Assert.AreEqual("Renée", read);
        Assert.AreEqual("Renée", string.Concat(mutation.Chunks));
        Assert.IsFalse(read.Contains('�'), "The split character was decoded as a replacement character.");
    }

    /// <summary>A reader with less room than there is to give gets the rest on the next read,
    /// rather than losing it.</summary>
    [TestMethod]
    public async Task Output_Larger_Than_The_Readers_Buffer_Is_Handed_Over_In_Pieces()
    {
        await using RestoringStream stream = new(
            new ChunkedStream([Encoding.UTF8.GetBytes("abcdefghij")]),
            new HoldingMutation(chunk => chunk),
            Encoding.UTF8);

        byte[] buffer = new byte[3];
        StringBuilder read = new();

        for (int taken = await stream.ReadAsync(buffer); taken > 0; taken = await stream.ReadAsync(buffer))
        {
            read.Append(Encoding.UTF8.GetString(buffer, 0, taken));
        }

        Assert.AreEqual("abcdefghij", read.ToString());
    }

    /// <summary>
    /// The origin is this stream's to close. The content it replaced is deliberately left
    /// undisposed -- disposing it would close this stream's own source -- so if this does not
    /// close it, the pooled connection behind it is held until a finalizer runs.
    /// </summary>
    [TestMethod]
    public async Task Disposing_Closes_The_Origin()
    {
        ChunkedStream origin = new([Encoding.UTF8.GetBytes("eins")]);

        await using (RestoringStream stream = new(origin, new HoldingMutation(chunk => chunk), Encoding.UTF8))
        {
            await ReadToEndAsync(stream);
        }

        Assert.IsTrue(origin.Disposed);
    }

    private static HoldingMutation Upper() => new(chunk => chunk.ToUpperInvariant());

    private static Task<string> ReadAllAsync(IExchangeStreamMutation mutation, params string[] chunks)
        => ReadAllAsync(mutation, [.. chunks.Select(Encoding.UTF8.GetBytes)]);

    private static async Task<string> ReadAllAsync(IExchangeStreamMutation mutation, params byte[][] chunks)
    {
        await using RestoringStream stream = new(new ChunkedStream(chunks), mutation, Encoding.UTF8);

        return await ReadToEndAsync(stream);
    }

    private static async Task<string> ReadToEndAsync(Stream stream)
    {
        using MemoryStream read = new();
        await stream.CopyToAsync(read);

        return Encoding.UTF8.GetString(read.ToArray());
    }

    /// <summary>An origin that hands over exactly the packets a test names, one per read, so a
    /// boundary falls where the test put it rather than where a buffer decides.</summary>
    private sealed class ChunkedStream(byte[][] chunks) : Stream
    {
        private int _index;

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_index >= chunks.Length)
                return ValueTask.FromResult(0);

            byte[] chunk = chunks[_index++];
            Assert.IsLessThanOrEqualTo(buffer.Length, chunk.Length, "The test's packet is larger than the stream's read buffer.");
            chunk.CopyTo(buffer);

            return ValueTask.FromResult(chunk.Length);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;

            base.Dispose(disposing);
        }
    }

    /// <summary>Runs the test's own chunk and flush callbacks, so a test can hold text back
    /// across a chunk boundary and release it later.</summary>
    private sealed class HoldingMutation(Func<string, string> onChunk, Func<string>? flush = null) : IExchangeStreamMutation
    {
        public string Mutate(string chunk) => onChunk(chunk);

        public string Flush() => flush?.Invoke() ?? string.Empty;
    }

    /// <summary>Passes the text through and remembers the pieces it was handed, so a test can
    /// assert where the boundaries ended up.</summary>
    private sealed class RecordingMutation : IExchangeStreamMutation
    {
        public List<string> Chunks { get; } = [];

        public string Mutate(string chunk)
        {
            if (chunk.Length > 0)
                Chunks.Add(chunk);

            return chunk;
        }

        public string Flush() => string.Empty;
    }
}
