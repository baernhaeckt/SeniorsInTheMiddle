using System.Text;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins how a body too large to rewrite is still forwarded whole.
///
/// A request body cannot be rewound, so measuring one means reading part of it. When the
/// measurement says "too large", those bytes are already off the socket and the only way the
/// destination gets a complete body is if they are served ahead of the rest. Losing or
/// duplicating a single byte here is a corrupted upload, and the buffered prefix is the part
/// that is easiest to get wrong.
/// </summary>
[TestClass]
public class PrefixedStreamTests
{
    private static async Task<string> ReadAllAsync(Stream stream, int bufferSize = 8192)
    {
        MemoryStream collected = new();
        byte[] buffer = new byte[bufferSize];

        while (true)
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0)
                break;

            collected.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(collected.ToArray());
    }

    [TestMethod]
    public async Task The_Prefix_Comes_First_Then_The_Rest()
    {
        using MemoryStream rest = new("world"u8.ToArray());
        using PrefixedStream stream = new("hello "u8.ToArray(), rest, leaveRestOpen: true);

        Assert.AreEqual("hello world", await ReadAllAsync(stream));
    }

    /// <summary>Nothing was read before the decision, so the stream is just the rest.</summary>
    [TestMethod]
    public async Task An_Empty_Prefix_Serves_Only_The_Rest()
    {
        using MemoryStream rest = new("world"u8.ToArray());
        using PrefixedStream stream = new([], rest, leaveRestOpen: true);

        Assert.AreEqual("world", await ReadAllAsync(stream));
    }

    /// <summary>The whole body fit in the measurement, and the socket has nothing left.</summary>
    [TestMethod]
    public async Task An_Empty_Rest_Serves_Only_The_Prefix()
    {
        using MemoryStream rest = new([]);
        using PrefixedStream stream = new("hello"u8.ToArray(), rest, leaveRestOpen: true);

        Assert.AreEqual("hello", await ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Everything_Empty_Is_End_Of_Stream_At_Once()
    {
        using MemoryStream rest = new([]);
        using PrefixedStream stream = new([], rest, leaveRestOpen: true);

        Assert.AreEqual(0, await stream.ReadAsync(new byte[16]));
    }

    /// <summary>
    /// A reader with a small buffer crosses the seam mid-prefix, which is the case that
    /// duplicates or drops bytes if the consumed count is wrong. One byte at a time is the
    /// extreme of it and the cheapest to assert.
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(11)]
    [DataRow(64)]
    public async Task The_Seam_Is_Crossed_Cleanly_Whatever_The_Buffer_Size(int bufferSize)
    {
        using MemoryStream rest = new("world"u8.ToArray());
        using PrefixedStream stream = new("hello "u8.ToArray(), rest, leaveRestOpen: true);

        Assert.AreEqual("hello world", await ReadAllAsync(stream, bufferSize));
    }

    /// <summary>Both synchronous overloads have to agree with the asynchronous ones; which
    /// one the forwarder takes is not this stream's choice.</summary>
    [TestMethod]
    public void The_Synchronous_Read_Crosses_The_Seam_Too()
    {
        using MemoryStream rest = new("world"u8.ToArray());
        using PrefixedStream stream = new("hello "u8.ToArray(), rest, leaveRestOpen: true);

        StringBuilder collected = new();
        byte[] buffer = new byte[4];

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            collected.Append(Encoding.UTF8.GetString(buffer, 0, read));

        Assert.AreEqual("hello world", collected.ToString());
    }

    /// <summary>
    /// Kestrel's body stream supports reading into an empty buffer as "tell me when data
    /// arrives". Answering that from the prefix -- which is what a switch on "bytes copied"
    /// rather than "prefix remains" would do -- sends the caller to the socket to wait for
    /// bytes the client already sent, and the request hangs.
    /// </summary>
    [TestMethod]
    public async Task An_Empty_Read_Does_Not_Skip_Past_The_Prefix()
    {
        using MemoryStream rest = new("world"u8.ToArray());
        using PrefixedStream stream = new("hello "u8.ToArray(), rest, leaveRestOpen: true);

        Assert.AreEqual(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.AreEqual("hello world", await ReadAllAsync(stream));
    }

    /// <summary>
    /// Who owns the rest differs by direction and is stated rather than assumed: a request
    /// body belongs to Kestrel, and closing it would end the connection under the server.
    /// </summary>
    [TestMethod]
    public void A_Borrowed_Rest_Survives_Disposal()
    {
        MemoryStream rest = new("world"u8.ToArray());

        using (PrefixedStream stream = new("hello "u8.ToArray(), rest, leaveRestOpen: true))
        {
            _ = stream.Read(new byte[16], 0, 16);
        }

        Assert.IsTrue(rest.CanRead, "The stream this one borrowed was closed under its owner.");
        rest.Dispose();
    }

    /// <summary>
    /// A response body is this stream's to close: leaving it open keeps its pooled connection
    /// out of circulation, which is a leak that only shows up under load.
    /// </summary>
    [TestMethod]
    public void An_Owned_Rest_Is_Closed_With_It()
    {
        MemoryStream rest = new("world"u8.ToArray());

        using (PrefixedStream stream = new("hello "u8.ToArray(), rest, leaveRestOpen: false))
        {
            _ = stream.Read(new byte[16], 0, 16);
        }

        Assert.IsFalse(rest.CanRead, "The stream this one owned was left open.");
    }

    /// <summary>It stands in for a request body, so it reads forward and nothing else.</summary>
    [TestMethod]
    public void It_Is_Read_Only_And_Forward_Only()
    {
        using MemoryStream rest = new([]);
        using PrefixedStream stream = new([], rest, leaveRestOpen: true);

        Assert.IsTrue(stream.CanRead);
        Assert.IsFalse(stream.CanSeek);
        Assert.IsFalse(stream.CanWrite);

        Assert.ThrowsExactly<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(0));
        Assert.ThrowsExactly<NotSupportedException>(() => _ = stream.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => _ = stream.Position);
    }
}
