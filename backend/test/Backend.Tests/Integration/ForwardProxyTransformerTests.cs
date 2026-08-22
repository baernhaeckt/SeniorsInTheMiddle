using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

using Microsoft.AspNetCore.Http;

using Yarp.ReverseProxy.Forwarder;

// Aliased rather than imported: Microsoft.Net.Http.Headers also defines MediaTypeHeaderValue,
// and the content headers below are built with the System.Net.Http.Headers one.
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace Backend.Tests.Integration;

/// <summary>
/// What leaves the proxy, seen from the destination server over a real socket, with the
/// mutation that changes nothing. A request that is not rewritten has to arrive exactly as it
/// was sent, framing and integrity headers included, whatever the proxy did while looking at
/// it. What happens once a mutation does change the bytes is in
/// <see cref="RequestBodyRewriteTests"/>.
///
/// <see cref="Replacing_The_Outgoing_Content_Is_Refused_By_The_Forwarder"/> pins the
/// constraint that decides how a rewrite has to be written at all.
/// </summary>
[TestClass]
public class ForwardProxyTransformerTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task Absolute_Form_Target_Reaches_The_Destination_Unchanged()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(harness.DestinationUri, "/api/items?page=2&q=b%C3%A4r"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        Assert.AreEqual("/api/items?page=2&q=b%C3%A4r", received.Target);
        Assert.AreEqual(harness.DestinationUri.Authority, received.Host);
    }

    /// <summary>
    /// The transform clears Host so it is recomputed from the destination. Forwarding the
    /// client's Host would send the proxy's own name to the destination, which name-based
    /// virtual hosts route on.
    /// </summary>
    [TestMethod]
    public async Task Host_Is_The_Destination_And_Not_The_Proxy()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        Assert.AreEqual(harness.DestinationUri.Authority, received.Host);
        Assert.AreNotEqual(harness.ProxyUri.Authority, received.Host);
    }

    [TestMethod]
    public async Task Body_Arrives_Byte_Exact()
    {
        const string payload = """{"customer":"Hans Muster","iban":"CH93 0076 2011 6238 5295 7"}""";

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using ByteArrayContent content = new(Encoding.UTF8.GetBytes(payload));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(harness.DestinationUri, "/orders"), content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(payload), received.Body);
        Framing.MatchesBody(received);
        Framing.IsUnambiguous(received);
    }

    /// <summary>
    /// Swiss addresses and names are full of characters that are two or three bytes in UTF-8.
    /// A rewrite that sets Content-Length from a string's Length rather than its encoded byte
    /// count truncates the body at the destination, and the destination is the only place the
    /// mistake shows.
    /// </summary>
    [TestMethod]
    public async Task Non_Ascii_Body_Is_Framed_By_Byte_Count_Not_Character_Count()
    {
        const string payload = """{"name":"Grüezi Müller","city":"Zürich","note":"🇨🇭"}""";
        byte[] expected = Encoding.UTF8.GetBytes(payload);

        Assert.AreNotEqual(payload.Length, expected.Length, "The fixture no longer exercises multi-byte characters.");

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using ByteArrayContent content = new(expected);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(harness.DestinationUri, "/customers"), content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(expected, received.Body);
        Assert.AreEqual(expected.Length.ToString(), received.Value(HeaderNames.ContentLength));
        Framing.IsUnambiguous(received);
    }

    /// <summary>
    /// Content-Type and Content-Language live on HttpContent, not on the request, so they are
    /// the two that disappear when a rewrite swaps the content out and only remembers to put
    /// the length back.
    /// </summary>
    [TestMethod]
    public async Task Content_Type_And_Content_Language_Survive_The_Transform()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using ByteArrayContent content = new("<hallo/>"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };
        content.Headers.ContentLanguage.Add("de-CH");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(harness.DestinationUri, "/documents"), content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        Assert.AreEqual("application/xml; charset=utf-8", received.Value(HeaderNames.ContentType));
        Assert.AreEqual("de-CH", received.Value(HeaderNames.ContentLanguage));
    }

    /// <summary>
    /// A GET has no body, so the outgoing request must carry no framing at all. Inventing an
    /// empty one adds Content-Length: 0 or, worse, Transfer-Encoding: chunked, and servers
    /// that do not expect a body on a GET answer that with 400.
    /// </summary>
    [TestMethod]
    public async Task Bodyless_Request_Carries_No_Framing_Headers()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/status"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        Assert.IsEmpty(received.Body);
        Assert.IsFalse(received.Has(HeaderNames.ContentLength), "A GET was given a Content-Length.");
        Assert.IsFalse(received.Has(HeaderNames.TransferEncoding), "A GET was given a Transfer-Encoding.");
    }

    [TestMethod]
    public async Task Empty_Body_Stays_Empty_And_Stays_Unambiguous()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using ByteArrayContent content = new([]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(harness.DestinationUri, "/ping"), content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        Assert.IsEmpty(received.Body);
        Assert.AreEqual("0", received.Value(HeaderNames.ContentLength));
        Assert.AreEqual("application/json", received.Value(HeaderNames.ContentType));
        Assert.IsFalse(received.Has(HeaderNames.TransferEncoding), "An empty body was turned into a chunked one.");
        Framing.IsUnambiguous(received);
    }

    /// <summary>
    /// A client that streams sends no Content-Length. The proxy must not invent one, and must
    /// not leave both framings in place -- see <see cref="AssertFramingIsUnambiguous"/>.
    /// </summary>
    [TestMethod]
    public async Task Chunked_Request_Arrives_With_One_Framing_And_The_Whole_Body()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('x', 5000));

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using UnknownLengthContent content = new(payload);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(harness.DestinationUri, "/upload"))
        {
            Content = content,
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(payload, received.Body);
        Assert.AreEqual("chunked", received.Value(HeaderNames.TransferEncoding));
        Assert.IsFalse(received.Has(HeaderNames.ContentLength), "A streamed request was given a length it never declared.");
        Framing.IsUnambiguous(received);
    }

    /// <summary>
    /// Content-Encoding describes the bytes it travels with. Forwarding it while the body is
    /// passed through untouched is correct and is what happens today.
    ///
    /// The moment the body is rewritten this expectation inverts: a rewritten body is plain
    /// text, and a surviving "gzip" makes the destination try to inflate it and fail. This
    /// test is where that inversion has to be made deliberate rather than discovered.
    /// </summary>
    [TestMethod]
    public async Task Content_Encoding_Is_Forwarded_While_The_Body_Is_Not_Rewritten()
    {
        byte[] payload = [0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00];

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using ByteArrayContent content = new(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(harness.DestinationUri, "/compressed"), content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(payload, received.Body);
        Assert.AreEqual("gzip", received.Value(HeaderNames.ContentEncoding));
    }

    /// <summary>
    /// The client announces a body and then vanishes. The forwarder has to give up rather
    /// than wait for bytes that will never come; a transform that reads the client body must
    /// not turn this into a stuck request.
    /// </summary>
    [TestMethod]
    public async Task Client_Disconnect_Mid_Body_Does_Not_Hang()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync();

        using (TcpClient client = new())
        {
            await client.ConnectAsync(harness.ProxyUri.Host, harness.ProxyUri.Port);

            // Announces 64 bytes and sends seven, then resets the connection. LingerState
            // with a zero timeout is what makes the close an RST rather than a clean FIN,
            // which is the disconnect a proxy actually has to survive.
            client.LingerState = new LingerOption(enable: true, seconds: 0);

            string head =
                $"POST {harness.DestinationUri}late HTTP/1.1\r\n" +
                $"Host: {harness.DestinationUri.Authority}\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: 64\r\n" +
                "\r\n" +
                "partial";

            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(head));
            await client.GetStream().FlushAsync();
        }

        // Throws TimeoutException when the forwarder never finishes, which is the failure
        // this test exists for.
        ForwarderError error = await harness.NextCompletionAsync(CompletionTimeout);

        Assert.AreNotEqual(ForwarderError.None, error, "A truncated request body was reported as a success.");
    }

    /// <summary>
    /// The constraint that decides how a body rewrite has to be built on YARP 2.x.
    ///
    /// Assigning a new HttpContent to the outgoing request is rejected by the forwarder --
    /// "Replacing the YARP outgoing request HttpContent is not supported. You should
    /// configure the HttpContext.Request instead." -- and the rejection is not a crash: it is
    /// caught, reported as <see cref="ForwarderError.RequestCreation"/> and answered with
    /// 502, so it looks like an unreachable destination.
    ///
    /// A rewrite therefore has to replace HttpContext.Request.Body and correct
    /// HttpContext.Request.Headers before the base transform copies them, not swap the
    /// content out afterwards.
    /// </summary>
    [TestMethod]
    public async Task Replacing_The_Outgoing_Content_Is_Refused_By_The_Forwarder()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            transformerFactory: _ => new ContentReplacingTransformer("{\"redacted\":true}"u8.ToArray()));
        using HttpClient client = harness.CreateProxiedClient();

        using ByteArrayContent content = new("""{"iban":"CH93 0076 2011 6238 5295 7"}"""u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(harness.DestinationUri, "/orders"), content);

        ForwarderError error = await harness.NextCompletionAsync(CompletionTimeout);

        Assert.AreEqual(ForwarderError.RequestCreation, error);
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.IsNull(harness.Received, "The request reached the destination although the forwarder refused it.");
    }

    /// <summary>Sends a known payload without ever announcing its length, so the client has
    /// to fall back to chunked transfer encoding.</summary>
    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(payload).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    /// <summary>Does what the obvious body rewrite would do, so the forwarder's refusal of it
    /// is pinned rather than remembered.</summary>
    private sealed class ContentReplacingTransformer(byte[] payload) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            proxyRequest.Content = new ByteArrayContent(payload);
        }
    }
}
