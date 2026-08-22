using System.Buffers;
using System.IO.Pipelines;
using System.Text;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// How the proxy collects a CONNECT request head before it decides what the connection is.
///
/// Two things have to hold at once, and they pull against each other: the head has to be waited
/// for, because it routinely arrives in more than one segment, and the wait has to end, because
/// a client that sends half of one and goes quiet would otherwise hold the connection for good.
/// </summary>
[TestClass]
public class ConnectHeadReadTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Generous: it only has to be long enough that a working wait is not mistaken
    /// for a hang.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A CONNECT split across two TCP segments, which is routine once the request carries
    /// Proxy-Authorization or a long User-Agent. Reading once and giving up would hand the
    /// half-read request to Kestrel, which answers 400 and leaves the client with no tunnel.
    /// </summary>
    [TestMethod]
    public async Task A_Head_Split_Across_Segments_Is_Waited_For()
    {
        Pipe pipe = new();

        ValueTask<ConnectProxyMiddleware.RequestHead?> reading =
            ConnectProxyMiddleware.ReadHeadAsync(pipe.Reader, Bound, CancellationToken.None);

        await WriteAsync(pipe, "CONNECT api.example.com:443 HTTP/1.1\r\nHost: api.example.com\r\nProxy-Con");
        await WriteAsync(pipe, "nection: keep-alive\r\n\r\n");

        ConnectProxyMiddleware.RequestHead? head = await reading.AsTask().WaitAsync(Bound);

        Assert.IsNotNull(head);
        Assert.IsNotNull(head.Value.End, "The terminator was never found, so the head reads as incomplete.");

        string text = Encoding.ASCII.GetString(
            BuffersExtensions.ToArray(head.Value.Buffer.Slice(0, head.Value.End!.Value)));

        StringAssert.StartsWith(text, "CONNECT api.example.com:443 HTTP/1.1\r\n");
        StringAssert.EndsWith(text, "\r\n\r\n");
    }

    /// <summary>
    /// The other half of the bargain. Buffering the head here takes away the request-header
    /// deadline that a partial request used to inherit by being handed straight to Kestrel, so
    /// the wait carries its own -- otherwise a client that sends half a CONNECT and then stops
    /// holds a connection until it goes away by itself, and enough of them hold all of them.
    /// </summary>
    [TestMethod]
    public async Task A_Head_That_Never_Finishes_Gives_Up()
    {
        Pipe pipe = new();

        ValueTask<ConnectProxyMiddleware.RequestHead?> reading =
            ConnectProxyMiddleware.ReadHeadAsync(pipe.Reader, ShortTimeout, CancellationToken.None);

        // A request line and nothing more: never terminated, and well under the size cap, so
        // only the deadline can end this.
        await WriteAsync(pipe, "CONNECT api.example.com:443 HTTP/1.1\r\n");

        Assert.IsNull(await reading.AsTask().WaitAsync(Bound));
    }

    /// <summary>A client that disconnects mid-head ends the wait too, without waiting out the
    /// deadline first.</summary>
    [TestMethod]
    public async Task A_Client_That_Goes_Away_Mid_Head_Ends_The_Wait()
    {
        Pipe pipe = new();

        ValueTask<ConnectProxyMiddleware.RequestHead?> reading =
            ConnectProxyMiddleware.ReadHeadAsync(pipe.Reader, Bound, CancellationToken.None);

        await WriteAsync(pipe, "CONNECT api.example.com:443 HTTP/1.1\r\n");
        await pipe.Writer.CompleteAsync();

        // Completed rather than timed out: the head comes back incomplete for the caller to
        // hand on, rather than as the null that means "nothing left to answer".
        ConnectProxyMiddleware.RequestHead? head = await reading.AsTask().WaitAsync(Bound);

        Assert.IsNotNull(head);
        Assert.IsNull(head.Value.End);
    }

    private static async Task WriteAsync(Pipe pipe, string text)
    {
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(text));
        await pipe.Writer.FlushAsync();
    }
}
