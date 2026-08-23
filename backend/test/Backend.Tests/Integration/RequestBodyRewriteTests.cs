using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

using SeniorsInTheMiddle.Proxy.Forwarding;

using Yarp.ReverseProxy.Forwarder;

// Aliased rather than imported: Microsoft.Net.Http.Headers also defines MediaTypeHeaderValue,
// and the content headers below are built with the System.Net.Http.Headers one.
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace Backend.Tests.Integration;

/// <summary>
/// What the destination receives once a mutation actually changes the bytes.
///
/// Every assertion here is made against the request that arrived over a socket rather than
/// against the objects the proxy built, because that is the only place the failure shows: a
/// stale Content-Length makes the destination hang or answer 400, a surviving Content-Encoding
/// makes it inflate plain text, and a Content-Length beside a Transfer-Encoding is a smuggling
/// vector no in-process assertion can see.
/// </summary>
[TestClass]
public class RequestBodyRewriteTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Rewrites the body to <paramref name="replacement"/>, whatever arrives.</summary>
    private static IBodyMutationFactory Replacing(string replacement)
        => new DelegateMutationFactory(onRequest: (_, _) => Encoding.UTF8.GetBytes(replacement));

    /// <summary>Rewrites every body to nothing at all.</summary>
    private static IBodyMutationFactory Emptying()
        => new DelegateMutationFactory(onRequest: (_, _) => []);

    private static async Task<HttpResponseMessage> PostAsync(
        ForwardingHarness harness,
        string path,
        byte[] payload,
        Action<HttpContentHeaders>? headers = null)
    {
        using HttpClient client = harness.CreateProxiedClient();
        using ByteArrayContent content = new(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        headers?.Invoke(content.Headers);

        return await client.PostAsync(new Uri(harness.DestinationUri, path), content);
    }

    /// <summary>
    /// The regression the rewrite exists to prevent, in the direction where the mistake is
    /// invisible locally: a longer body behind the original, shorter length arrives truncated,
    /// and the destination reads a body that parses as valid JSON with fields missing.
    /// </summary>
    [TestMethod]
    public async Task Body_That_Grows_Is_Reframed_To_Its_New_Length()
    {
        const string replacement = """{"customer":"Anna Beispiel-Muster","iban":"CH00 0000 0000 0000 0000 0","note":"padded"}""";

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(Replacing(replacement));

        using HttpResponseMessage response = await PostAsync(harness, "/orders", """{"a":1}"""u8.ToArray());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(replacement), received.Body);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(replacement).ToString(), received.Value(HeaderNames.ContentLength));
        Framing.MatchesBody(received);
        Framing.IsUnambiguous(received);
    }

    /// <summary>
    /// The other direction: a shorter body behind the original, longer length leaves the
    /// destination waiting for bytes that will never come, which reads as a hung request
    /// rather than as a proxy bug.
    /// </summary>
    [TestMethod]
    public async Task Body_That_Shrinks_Is_Reframed_To_Its_New_Length()
    {
        const string replacement = """{"a":1}""";

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(Replacing(replacement));

        using HttpResponseMessage response = await PostAsync(
            harness,
            "/orders",
            Encoding.UTF8.GetBytes("""{"customer":"Hans Muster","iban":"CH93 0076 2011 6238 5295 7"}"""));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(replacement), received.Body);
        Assert.AreEqual(replacement.Length.ToString(), received.Value(HeaderNames.ContentLength));
        Framing.MatchesBody(received);
        Framing.IsUnambiguous(received);
    }

    /// <summary>
    /// A replacement that is shorter in characters but longer in bytes. Anything that framed
    /// the body by string length rather than by encoded byte count passes the two tests above
    /// and fails this one.
    /// </summary>
    [TestMethod]
    public async Task Non_Ascii_Replacement_Is_Framed_By_Byte_Count()
    {
        const string replacement = """{"city":"Zürich","name":"Grüezi Müller 🇨🇭"}""";
        byte[] expected = Encoding.UTF8.GetBytes(replacement);

        Assert.AreNotEqual(replacement.Length, expected.Length, "The fixture no longer exercises multi-byte characters.");

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(Replacing(replacement));

        using HttpResponseMessage response = await PostAsync(
            harness,
            "/customers",
            Encoding.UTF8.GetBytes("""{"city":"Bern","name":"Hans Muster and some more text"}"""));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(expected, received.Body);
        Assert.AreEqual(expected.Length.ToString(), received.Value(HeaderNames.ContentLength));
        Framing.MatchesBody(received);
    }

    /// <summary>
    /// A rewritten body is not the compressed body the header describes. Forwarding "gzip"
    /// beside plain text makes the destination fail to inflate it, usually as a 400 that says
    /// nothing about a proxy.
    /// </summary>
    [TestMethod]
    public async Task Rewriting_Drops_Content_Encoding()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(Replacing("""{"clean":true}"""));

        using HttpResponseMessage response = await PostAsync(
            harness,
            "/compressed",
            [0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00],
            headers => headers.ContentEncoding.Add("gzip"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual("""{"clean":true}"""u8.ToArray(), received.Body);
        Assert.IsFalse(received.Has(HeaderNames.ContentEncoding), "Content-Encoding outlived the bytes it described.");
        Framing.MatchesBody(received);
    }

    /// <summary>
    /// Digests are computed over the original bytes and cannot survive a rewrite. They are
    /// dropped rather than recomputed: recomputing would re-assert on the proxy's behalf an
    /// integrity claim the client never made about these bytes.
    /// </summary>
    [TestMethod]
    [DataRow("Content-MD5", "Q2hlY2sgSW50ZWdyaXR5IQ==")]
    [DataRow("Digest", "sha-256=:qqlAJmTxpB9A67xSyZk+tmrrNmYClY/fqig7ceZNsSM=:")]
    [DataRow("Content-Digest", "sha-256=:qqlAJmTxpB9A67xSyZk+tmrrNmYClY/fqig7ceZNsSM=:")]
    [DataRow("Repr-Digest", "sha-256=:qqlAJmTxpB9A67xSyZk+tmrrNmYClY/fqig7ceZNsSM=:")]
    [DataRow("Content-Range", "bytes 0-6/7")]
    public async Task Rewriting_Drops_Headers_That_Describe_The_Original_Bytes(string header, string value)
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(Replacing("""{"clean":true}"""));

        using HttpClient client = harness.CreateProxiedClient();
        using ByteArrayContent content = new("""{"a":1}"""u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.TryAddWithoutValidation(header, value);

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(harness.DestinationUri, "/documents"), content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        Assert.IsFalse(received.Has(header), $"{header} outlived the bytes it described.");
    }

    /// <summary>
    /// Content-Type and Content-Language describe the message, not the bytes, so they survive.
    /// Content-Type is forwarded verbatim rather than rebuilt, which is what keeps the charset
    /// parameter exactly as the client wrote it: constructing it fresh from a media type and an
    /// encoding appends "; charset=utf-8", and strict destinations reject that.
    /// </summary>
    [TestMethod]
    public async Task Rewriting_Keeps_Content_Type_Verbatim_And_Content_Language()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(Replacing("<sauber/>"));

        using HttpClient client = harness.CreateProxiedClient();
        using ByteArrayContent content = new("<hallo/>"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        content.Headers.ContentLanguage.Add("de-CH");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(harness.DestinationUri, "/documents"), content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        Assert.AreEqual("application/xml", received.Value(HeaderNames.ContentType));
        Assert.AreEqual("de-CH", received.Value(HeaderNames.ContentLanguage));
    }

    /// <summary>
    /// A chunked request that is rewritten goes out with a length, because the length is now
    /// known. The Transfer-Encoding has to go with it -- both framings on one message is the
    /// smuggling vector, and it is also what the base transform reads to decide whether to drop
    /// the Content-Length again.
    /// </summary>
    [TestMethod]
    public async Task Rewriting_A_Chunked_Request_Sends_A_Length_And_Stops_Chunking()
    {
        const string replacement = """{"clean":true}""";

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(Replacing(replacement));
        using HttpClient client = harness.CreateProxiedClient();

        using UnknownLengthContent content = new(Encoding.UTF8.GetBytes(new string('x', 4096)));
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(harness.DestinationUri, "/upload"))
        {
            Content = content,
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(replacement), received.Body);
        Assert.AreEqual(replacement.Length.ToString(), received.Value(HeaderNames.ContentLength));
        Assert.IsFalse(received.Has(HeaderNames.TransferEncoding), "The rewritten body was still announced as chunked.");
        Framing.IsUnambiguous(received);
    }

    /// <summary>
    /// A GET has no body to offer. Handing the mutation an empty array would invite it to
    /// return something, and a GET with a Content-Length is answered with 400 by servers that
    /// do not expect a body.
    /// </summary>
    [TestMethod]
    public async Task Bodyless_Request_Is_Never_Offered_To_The_Mutation()
    {
        bool offered = false;
        DelegateMutationFactory mutation = new(onRequest: (_, _) =>
        {
            offered = true;
            return null;
        });

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(mutation);
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/status"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(offered, "A request that cannot have a body was offered one.");

        RecordedRequest received = harness.RequireReceived();
        Assert.IsFalse(received.Has(HeaderNames.ContentLength));
        Assert.IsFalse(received.Has(HeaderNames.TransferEncoding));
    }

    /// <summary>
    /// A body a signature is computed over cannot be rewritten: the destination checks the
    /// signature against bytes that no longer match and answers 401 or 403 without saying why.
    /// The original is forwarded and the skip is logged, so an uninspected body is a line in
    /// the log rather than something to be inferred.
    /// </summary>
    [TestMethod]
    [DataRow("x-amz-content-sha256", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [DataRow("PayPal-Transmission-Sig", "thy4/U0TgNZbMrfPetVQnCUgL5DXvsE5cE+PtVAeaeMJiPMTJPPINaLcVBmy1D2Nw==")]
    [DataRow("Signature", "sig1=:dGVzdA==:")]
    [DataRow("Signature-Input", "sig1=(\"@method\" \"content-digest\");created=1700000000")]
    [DataRow("X-Hub-Signature-256", "sha256=7d38cdd689735b008b3c702edd92593c6b2e0e6f2ede83bf5c17c1c0e9f0bcbf")]
    [DataRow("Stripe-Signature", "t=1700000000,v1=5257a869e7ecebeda32affa62cdca3fa51cad7e77a0e56ff536d0ce8e108d8bd")]
    public async Task Signed_Body_Is_Forwarded_Untouched_And_Reported(string header, string value)
    {
        byte[] payload = """{"customer":"Hans Muster"}"""u8.ToArray();

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(Replacing("""{"clean":true}"""));

        using HttpClient client = harness.CreateProxiedClient();
        using ByteArrayContent content = new(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(harness.DestinationUri, "/signed"))
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation(header, value);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(payload, received.Body);
        Assert.AreEqual(payload.Length.ToString(), received.Value(HeaderNames.ContentLength));
        Assert.IsTrue(
            harness.Warnings.Any(warning => warning.Contains(header, StringComparison.OrdinalIgnoreCase)),
            $"Skipping the signed body was not logged. Warnings: {string.Join(" / ", harness.Warnings)}");
    }

    /// <summary>An Authorization header can carry the signature instead of a dedicated one.</summary>
    [TestMethod]
    [DataRow("AWS4-HMAC-SHA256 Credential=AKIA/20240101/eu-central-1/s3/aws4_request, Signature=abc", true)]
    [DataRow("Signature keyId=\"test\",signature=\"abc\"", true)]
    [DataRow("Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc", false)]
    [DataRow("Basic aGFuczpnZWhlaW0=", false)]
    public async Task Authorization_Scheme_Decides_Whether_The_Body_May_Be_Rewritten(
        string authorization,
        bool signsTheBody)
    {
        byte[] payload = """{"customer":"Hans Muster"}"""u8.ToArray();
        const string replacement = """{"clean":true}""";

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(Replacing(replacement));

        using HttpClient client = harness.CreateProxiedClient();
        using ByteArrayContent content = new(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(harness.DestinationUri, "/signed"))
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation(HeaderNames.Authorization, authorization);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(
            signsTheBody ? payload : Encoding.UTF8.GetBytes(replacement),
            received.Body);
        Framing.HasLength(received);
    }

    /// <summary>
    /// Past the limit the body streams through untouched. The bytes already read while
    /// measuring it have to be served ahead of the rest, or the destination receives a body
    /// with a hole at the front and a Content-Length that no longer matches.
    /// </summary>
    [TestMethod]
    public async Task Body_Over_The_Limit_Is_Forwarded_Whole_And_Reported()
    {
        // Deliberately not a multiple of the read buffer, so a prefix that is served twice or
        // not at all shifts the whole body rather than landing on a boundary.
        byte[] payload = new byte[20_001];
        Random.Shared.NextBytes(payload);

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            Replacing("""{"clean":true}"""),
            new BodyLimits(4096));

        using HttpResponseMessage response = await PostAsync(harness, "/upload", payload);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(payload, received.Body);
        Assert.AreEqual(payload.Length.ToString(), received.Value(HeaderNames.ContentLength));
        Assert.IsTrue(
            harness.Warnings.Any(warning => warning.Contains("4096", StringComparison.Ordinal)),
            $"Skipping the oversized body was not logged. Warnings: {string.Join(" / ", harness.Warnings)}");
        Framing.IsUnambiguous(received);
    }

    /// <summary>A body exactly at the limit is still rewritten; the limit is inclusive.</summary>
    [TestMethod]
    public async Task Body_Exactly_At_The_Limit_Is_Still_Rewritten()
    {
        const string replacement = """{"clean":true}""";

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            Replacing(replacement),
            new BodyLimits(4096));

        using HttpResponseMessage response = await PostAsync(harness, "/upload", new byte[4096]);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(replacement), received.Body);
        Assert.IsEmpty(harness.Warnings);
    }

    /// <summary>A limit of zero turns rewriting off without taking it out of the pipeline.</summary>
    [TestMethod]
    public async Task Limit_Of_Zero_Forwards_Every_Body_Untouched()
    {
        byte[] payload = """{"customer":"Hans Muster"}"""u8.ToArray();

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            Replacing("""{"clean":true}"""),
            new BodyLimits(0));

        using HttpResponseMessage response = await PostAsync(harness, "/orders", payload);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(payload, received.Body);
        Framing.HasLength(received);

        // Silently. Measuring a body against a limit of zero reads a byte off it and finds it
        // over the limit every time, so a setting meant to disable rewriting logged an
        // "uninspected" warning for every single request that carried a body.
        Assert.IsEmpty(harness.Warnings);
    }

    /// <summary>
    /// The encoding is named rather than guessed. A mutation that decoded with UTF-8
    /// regardless would read mojibake out of a Latin-1 body and write it back.
    /// </summary>
    [TestMethod]
    [DataRow("application/json; charset=iso-8859-1", "iso-8859-1")]
    [DataRow("application/json; charset=utf-8", "utf-8")]
    [DataRow("application/json", "utf-8")]
    [DataRow("application/json; charset=made-up", "utf-8")]
    // Only the UTF family, ASCII and Latin-1 ship with this runtime, so a legacy Windows code
    // page falls back to UTF-8. The transform logs the substitution; see the assertion below.
    [DataRow("application/json; charset=windows-1252", "utf-8")]
    public async Task Mutation_Is_Told_The_Declared_Charset(string contentType, string expectedEncoding)
    {
        string? seen = null;
        DelegateMutationFactory mutation = new(onRequest: (_, descriptor) =>
        {
            seen = descriptor.Encoding.WebName;
            return null;
        });

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(mutation);

        using HttpClient client = harness.CreateProxiedClient();
        using ByteArrayContent content = new("""{"a":1}"""u8.ToArray());
        content.Headers.TryAddWithoutValidation(HeaderNames.ContentType, contentType);

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(harness.DestinationUri, "/orders"), content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(expectedEncoding, seen);
    }

    /// <summary>
    /// The mutation is handed the client's bytes, not a decoded and re-encoded copy of them.
    /// </summary>
    [TestMethod]
    public async Task Mutation_Is_Handed_The_Bytes_As_They_Arrived()
    {
        byte[] payload = [0x00, 0x01, 0xfe, 0xff, 0x7f, 0x80];
        byte[]? seen = null;

        DelegateMutationFactory mutation = new(onRequest: (body, _) =>
        {
            seen = body.ToArray();
            return null;
        });

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(mutation);

        using HttpResponseMessage response = await PostAsync(harness, "/binary", payload);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(seen);
        CollectionAssert.AreEqual(payload, seen);
    }

    /// <summary>
    /// The client announces a body and vanishes while the transform is buffering it. The
    /// forwarder has to give up rather than wait for bytes that will never arrive.
    /// </summary>
    [TestMethod]
    public async Task Client_Disconnect_While_Buffering_Does_Not_Hang()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(Replacing("""{"clean":true}"""));

        using (TcpClient client = new())
        {
            await client.ConnectAsync(harness.ProxyUri.Host, harness.ProxyUri.Port);

            // Announces 64 bytes and sends seven, then resets. LingerState with a zero timeout
            // makes the close an RST rather than a clean FIN, which is the disconnect a proxy
            // actually has to survive.
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

        // Throws TimeoutException when the transform never lets go, which is the failure this
        // test exists for: the buffering read is ours, so a hang here would be ours too.
        ForwarderError error = await harness.NextCompletionAsync(CompletionTimeout);

        Assert.AreNotEqual(ForwarderError.None, error, "A truncated request body was reported as a success.");
        Assert.IsNull(harness.Received, "A body that was never fully received was forwarded anyway.");
    }

    /// <summary>
    /// A mutation is allowed to refuse a body it cannot parse by throwing, and the interface
    /// says so. What the client then sees is pinned here: 502, nothing forwarded, and no
    /// echo of the exception, which would otherwise leak the proxy's internals to whoever
    /// sent the malformed body.
    /// </summary>
    [TestMethod]
    public async Task Mutation_That_Throws_Sends_Nothing_And_Answers_502()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(new ThrowingBodyMutation());

        using HttpResponseMessage response = await PostAsync(harness, "/orders", """{"a":1}"""u8.ToArray());

        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);

        ForwarderError error = await harness.NextCompletionAsync(CompletionTimeout);
        Assert.AreEqual(ForwarderError.RequestCreation, error);
        Assert.IsNull(harness.Received, "A body the mutation refused was forwarded anyway.");

        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ThrowingBodyMutation.Message, body);
    }

    /// <summary>
    /// The shape the limit actually exists for: a streamed upload with no declared length.
    /// Nothing downstream can length-check it, so a prefix served twice or not at all would
    /// corrupt the body silently. It is the one case where <c>PrefixedStream</c> and the
    /// chunked framing have to work together.
    /// </summary>
    [TestMethod]
    public async Task Oversized_Chunked_Body_Is_Forwarded_Whole_And_Stays_Chunked()
    {
        // Not a multiple of the read buffer, so a prefix off by anything shifts the body
        // rather than landing on a boundary.
        byte[] payload = new byte[20_001];
        Random.Shared.NextBytes(payload);

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            Replacing("""{"clean":true}"""),
            new BodyLimits(4096));
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
        Assert.IsFalse(received.Has(HeaderNames.ContentLength), "A streamed body was given a length it never declared.");
        Assert.IsTrue(
            harness.Warnings.Any(warning => warning.Contains("4096", StringComparison.Ordinal)),
            $"Skipping the oversized body was not logged. Warnings: {string.Join(" / ", harness.Warnings)}");
    }

    /// <summary>
    /// "Return nothing when the body cannot be salvaged" is a plausible redaction policy, and
    /// it is the one length a proxy can get wrong in both directions at once: the outgoing
    /// request has to say zero rather than omit the length or fall back to chunked.
    /// </summary>
    [TestMethod]
    public async Task Mutation_Returning_An_Empty_Body_Frames_It_As_Zero_Length()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(Emptying());

        using HttpResponseMessage response = await PostAsync(
            harness,
            "/orders",
            """{"customer":"Hans Muster"}"""u8.ToArray());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        Assert.IsEmpty(received.Body);
        Assert.AreEqual("0", received.Value(HeaderNames.ContentLength));
        Assert.IsFalse(received.Has(HeaderNames.TransferEncoding), "An empty body was announced as chunked.");
        Framing.IsUnambiguous(received);
    }

    /// <summary>
    /// A charset this runtime does not carry is substituted with UTF-8, and the substitution
    /// is logged. Without the log a mutation would be handed the wrong alphabet and the only
    /// evidence would be mojibake at the destination.
    /// </summary>
    [TestMethod]
    public async Task Unavailable_Charset_Is_Reported_Rather_Than_Silently_Substituted()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync();

        using HttpClient client = harness.CreateProxiedClient();
        using ByteArrayContent content = new("""{"a":1}"""u8.ToArray());
        content.Headers.TryAddWithoutValidation(HeaderNames.ContentType, "application/json; charset=windows-1252");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(harness.DestinationUri, "/orders"), content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(
            harness.Warnings.Any(warning => warning.Contains("windows-1252", StringComparison.OrdinalIgnoreCase)),
            $"The charset substitution was not logged. Warnings: {string.Join(" / ", harness.Warnings)}");
    }

    /// <summary>Sends a known payload without ever announcing its length, so the client has to
    /// fall back to chunked transfer encoding.</summary>
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

    /// <summary>A mutation that fails on every body it is given.</summary>
    private sealed class ThrowingBodyMutation : IBodyMutationFactory, IExchangeBodyMutation
    {
        public const string Message = "the body is not the shape this mutation parses";

        public bool Rewrites => true;

        public IExchangeBodyMutation CreateForExchange(ClientIdentity client, Uri destination, IExchangeObserver observer) => this;

        public ValueTask<byte[]?> MutateRequestAsync(
            ReadOnlyMemory<byte> body,
            BodyDescriptor descriptor,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(Message);

        public ValueTask<byte[]?> MutateResponseAsync(
            ReadOnlyMemory<byte> body,
            BodyDescriptor descriptor,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<byte[]?>(null);
    }
}
