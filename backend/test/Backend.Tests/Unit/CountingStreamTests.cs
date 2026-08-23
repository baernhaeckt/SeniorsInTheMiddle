using System.Text;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins the byte count the dashboard shows for a response, and that taking it costs the
/// response nothing.
///
/// The count is the only honest size available: a chunked response carries no Content-Length,
/// which is the normal framing for dynamic HTML and most JSON APIs. So it is taken on the way
/// out -- which means every write path has to count, including the ones a caller picks for
/// performance and nobody thinks about again.
/// </summary>
[TestClass]
public class CountingStreamTests
{
    [TestMethod]
    public void Nothing_Written_Is_Zero_Bytes()
    {
        using MemoryStream sink = new();
        CountingStream counted = new(sink);

        Assert.AreEqual(0, counted.BytesWritten);
    }

    /// <summary>
    /// Four ways to write the same bytes. Stream picks between them by what the caller passes
    /// and whether the call is awaited, so a count that only covers one of them under-reports
    /// whenever the forwarder takes a different path.
    /// </summary>
    [TestMethod]
    public async Task Every_Write_Path_Counts_And_Passes_The_Bytes_Through()
    {
        using MemoryStream sink = new();
        CountingStream counted = new(sink);
        byte[] payload = "Grüezi"u8.ToArray();

        counted.Write(payload, 0, payload.Length);
        counted.Write(payload.AsSpan());
        await counted.WriteAsync(payload, 0, payload.Length);
        await counted.WriteAsync(payload.AsMemory());

        Assert.AreEqual(payload.Length * 4, counted.BytesWritten);
        Assert.AreEqual(
            string.Concat(Enumerable.Repeat("Grüezi", 4)),
            Encoding.UTF8.GetString(sink.ToArray()));
    }

    /// <summary>An offset write counts what it wrote, not the whole buffer.</summary>
    [TestMethod]
    public async Task A_Segment_Counts_Only_Its_Own_Length()
    {
        using MemoryStream sink = new();
        CountingStream counted = new(sink);
        byte[] payload = "0123456789"u8.ToArray();

        counted.Write(payload, 2, 3);
        await counted.WriteAsync(payload, 7, 2);

        Assert.AreEqual(5, counted.BytesWritten);
        Assert.AreEqual("23478", Encoding.UTF8.GetString(sink.ToArray()));
    }

    /// <summary>An empty write is a real thing on a flushed stream and must not disturb the count.</summary>
    [TestMethod]
    public async Task An_Empty_Write_Adds_Nothing()
    {
        using MemoryStream sink = new();
        CountingStream counted = new(sink);

        counted.Write([], 0, 0);
        await counted.WriteAsync(Array.Empty<byte>(), 0, 0);
        await counted.FlushAsync();
        counted.Flush();

        Assert.AreEqual(0, counted.BytesWritten);
    }

    /// <summary>
    /// A streamed response is many small writes, and the count is a long because a proxied
    /// download passes what an int holds. This walks a realistic number of chunks rather than
    /// one large one, since the count is per call.
    /// </summary>
    [TestMethod]
    public async Task Many_Chunks_Add_Up()
    {
        using MemoryStream sink = new();
        CountingStream counted = new(sink);
        byte[] chunk = new byte[4096];

        for (int written = 0; written < 512; written++)
            await counted.WriteAsync(chunk);

        Assert.AreEqual(512L * 4096, counted.BytesWritten);
        Assert.AreEqual(512L * 4096, sink.Length);
    }

    /// <summary>
    /// It wraps a response body and nothing else. Everything a reader or a seeker would want
    /// throws rather than answering something plausible, so a future caller that hands it to
    /// the wrong side of the pipeline finds out immediately.
    /// </summary>
    [TestMethod]
    public void It_Is_Write_Only()
    {
        using MemoryStream sink = new();
        CountingStream counted = new(sink);

        Assert.IsTrue(counted.CanWrite);
        Assert.IsFalse(counted.CanRead);
        Assert.IsFalse(counted.CanSeek);

        Assert.ThrowsExactly<NotSupportedException>(() => counted.Read(new byte[1], 0, 1));
        Assert.ThrowsExactly<NotSupportedException>(() => counted.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => counted.SetLength(0));
        Assert.ThrowsExactly<NotSupportedException>(() => _ = counted.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => _ = counted.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => counted.Position = 0);
    }

    /// <summary>Cancellation belongs to the stream underneath; the counter must not swallow it.</summary>
    [TestMethod]
    public async Task Cancellation_Reaches_The_Inner_Stream()
    {
        using MemoryStream sink = new();
        CountingStream counted = new(sink);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await counted.WriteAsync("x"u8.ToArray(), cancelled.Token));
    }
}
