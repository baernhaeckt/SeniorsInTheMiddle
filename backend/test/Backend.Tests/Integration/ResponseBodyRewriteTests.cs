using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;

using Microsoft.AspNetCore.Http;

using SeniorsInTheMiddle.Proxy.Forwarding;
using SeniorsInTheMiddle.Proxy.Telemetry;

// Aliased rather than imported: Microsoft.Net.Http.Headers also defines MediaTypeHeaderValue,
// and the content headers elsewhere are built with the System.Net.Http.Headers one.
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace Backend.Tests.Integration;

/// <summary>
/// What the client receives once a mutation changes the response on its way back.
///
/// A response is a harder thing to hold than a request. A request body is a finite document by
/// the time the proxy sees it; a response can be a protocol upgrade, a stream that never ends, or
/// a small archive of something enormous, and buffering any of those is a hang rather than a
/// mistake anyone sees in a log. Most of these tests are about the proxy declining to touch
/// something, and about it putting back exactly what it took whenever it does.
/// </summary>
[TestClass]
public class ResponseBodyRewriteTests
{
    private const string OriginalBody = """{"patient":"Hans Muster","ahv":"756.1234.5678.97"}""";

    /// <summary>Rewrites every response body to <paramref name="replacement"/>.</summary>
    private static IBodyMutationFactory ReplacingResponse(string replacement)
        => new DelegateMutationFactory(onResponse: (_, _) => Encoding.UTF8.GetBytes(replacement));

    /// <summary>An origin that answers with <paramref name="body"/> under <paramref name="contentType"/>.</summary>
    private static Func<HttpContext, byte[], Task> Answering(
        string body,
        string contentType = "application/json",
        string? contentEncoding = null)
        => async (context, _) =>
        {
            context.Response.ContentType = contentType;
            byte[] payload = Encoding.UTF8.GetBytes(body);

            if (contentEncoding == "gzip")
            {
                using MemoryStream compressed = new();
                using (GZipStream gzip = new(compressed, CompressionMode.Compress, leaveOpen: true))
                    await gzip.WriteAsync(payload, context.RequestAborted);

                payload = compressed.ToArray();
                context.Response.Headers.ContentEncoding = "gzip";
            }
            else if (contentEncoding is not null)
            {
                context.Response.Headers.ContentEncoding = contentEncoding;
            }

            context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload, context.RequestAborted);
        };

    [TestMethod]
    public async Task Response_Body_Is_Rewritten_And_Reframed()
    {
        const string replacement = """{"patient":"Anna Beispiel","ahv":"756.0000.0000.00","padded":true}""";

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            ReplacingResponse(replacement),
            respond: Answering(OriginalBody));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/patients"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(replacement, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(Encoding.UTF8.GetByteCount(replacement), response.Content.Headers.ContentLength);
    }

    /// <summary>
    /// The shrinking direction, which is the one that hangs a client rather than truncating it:
    /// the origin's longer Content-Length would leave the browser waiting for bytes that are
    /// never sent.
    /// </summary>
    [TestMethod]
    public async Task Response_That_Shrinks_Is_Reframed_To_Its_New_Length()
    {
        const string replacement = """{"ok":1}""";

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            ReplacingResponse(replacement),
            respond: Answering(OriginalBody));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/patients"));

        Assert.AreEqual(replacement, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(replacement.Length, response.Content.Headers.ContentLength);
    }

    /// <summary>
    /// A response nothing changed has to arrive exactly as the origin sent it, headers included.
    /// The proxy read it, which no one downstream should be able to tell.
    /// </summary>
    [TestMethod]
    public async Task Unchanged_Response_Arrives_Exactly_As_It_Was_Sent()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            respond: Answering(OriginalBody));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/patients"));

        Assert.AreEqual(OriginalBody, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(Encoding.UTF8.GetByteCount(OriginalBody), response.Content.Headers.ContentLength);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>The mutation works on text, so a compressed body has to be handed over decoded.</summary>
    [TestMethod]
    public async Task Compressed_Response_Reaches_The_Mutation_As_Plaintext()
    {
        byte[]? seen = null;
        DelegateMutationFactory mutation = new(onResponse: (body, _) =>
        {
            seen = body.ToArray();
            return null;
        });

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            mutation,
            respond: Answering(OriginalBody, contentEncoding: "gzip"));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/patients"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(seen);
        Assert.AreEqual(OriginalBody, Encoding.UTF8.GetString(seen));
    }

    /// <summary>
    /// A compressed response nobody changed still travels compressed. Decoding it to look at it
    /// must not cost the client the compression it was going to get.
    /// </summary>
    [TestMethod]
    public async Task Unchanged_Compressed_Response_Stays_Compressed()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            respond: Answering(OriginalBody, contentEncoding: "gzip"));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/patients"));

        Assert.AreEqual("gzip", string.Join(",", response.Content.Headers.ContentEncoding));

        using GZipStream decompressed = new(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using StreamReader reader = new(decompressed);

        Assert.AreEqual(OriginalBody, await reader.ReadToEndAsync());
    }

    /// <summary>
    /// A rewritten body is plaintext, so the encoding that described the old bytes has to go with
    /// them. Leaving it makes the browser try to inflate text and fail.
    /// </summary>
    [TestMethod]
    public async Task Rewriting_A_Compressed_Response_Drops_The_Encoding()
    {
        const string replacement = """{"patient":"Anna Beispiel"}""";

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            ReplacingResponse(replacement),
            respond: Answering(OriginalBody, contentEncoding: "gzip"));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/patients"));

        Assert.IsEmpty(response.Content.Headers.ContentEncoding);
        Assert.AreEqual(replacement, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(Encoding.UTF8.GetByteCount(replacement), response.Content.Headers.ContentLength);
    }

    /// <summary>
    /// An encoding this runtime cannot undo is forwarded exactly as it came. Reading it is
    /// impossible and guessing would be worse, so the skip is logged instead.
    /// </summary>
    [TestMethod]
    public async Task Response_Under_An_Unknown_Encoding_Is_Forwarded_Untouched()
    {
        byte[] payload = Encoding.UTF8.GetBytes(OriginalBody);

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            ReplacingResponse("""{"never":true}"""),
            respond: Answering(OriginalBody, contentEncoding: "zstd"));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/patients"));

        CollectionAssert.AreEqual(payload, await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual("zstd", string.Join(",", response.Content.Headers.ContentEncoding));
        Assert.IsTrue(
            harness.Warnings.Any(warning => warning.Contains("zstd", StringComparison.OrdinalIgnoreCase)),
            $"The unreadable encoding was not logged. Warnings: {string.Join(" / ", harness.Warnings)}");
    }

    /// <summary>
    /// A body that claims an encoding it is not. Failing the response would break the page; the
    /// client gets the bytes and the chance to make its own sense of them.
    /// </summary>
    [TestMethod]
    public async Task Response_That_Lies_About_Its_Encoding_Is_Forwarded_Untouched()
    {
        byte[] payload = Encoding.UTF8.GetBytes(OriginalBody);

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            ReplacingResponse("""{"never":true}"""),
            respond: Answering(OriginalBody, contentEncoding: "gzip-but-not-really"));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/patients"));

        CollectionAssert.AreEqual(payload, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// Most of what a browser fetches cannot carry a person's details and is expensive to hold.
    /// Those bodies are passed through without ever being read.
    /// </summary>
    [TestMethod]
    [DataRow("image/png")]
    [DataRow("font/woff2")]
    [DataRow("application/octet-stream")]
    [DataRow("video/mp4")]
    public async Task Uninspectable_Media_Types_Are_Never_Offered(string contentType)
    {
        bool offered = false;
        DelegateMutationFactory mutation = new(onResponse: (_, _) =>
        {
            offered = true;
            return "{}"u8.ToArray();
        });

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            mutation,
            respond: Answering(OriginalBody, contentType));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/asset"));

        Assert.IsFalse(offered, $"{contentType} was opened.");
        Assert.AreEqual(OriginalBody, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("application/json")]
    [DataRow("application/ld+json")]
    [DataRow("application/problem+json")]
    [DataRow("text/html")]
    [DataRow("text/plain")]
    [DataRow("application/xml")]
    [DataRow("application/x-www-form-urlencoded")]
    public async Task Inspectable_Media_Types_Are_Offered(string contentType)
    {
        bool offered = false;
        DelegateMutationFactory mutation = new(onResponse: (_, _) =>
        {
            offered = true;
            return null;
        });

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            mutation,
            respond: Answering(OriginalBody, contentType));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/document"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(offered, $"{contentType} was not opened.");
    }

    /// <summary>
    /// An event stream is text and never ends. Buffering one holds the client until the stream
    /// closes, which for this media type is never, so it is the one text type that is refused.
    /// </summary>
    [TestMethod]
    public async Task Event_Stream_Is_Never_Buffered()
    {
        bool offered = false;
        DelegateMutationFactory mutation = new(onResponse: (_, _) =>
        {
            offered = true;
            return null;
        });

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(mutation, respond: async (context, _) =>
        {
            context.Response.ContentType = "text/event-stream";
            for (int index = 0; index < 3; index++)
            {
                await context.Response.WriteAsync($"data: event-{index}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(250, context.RequestAborted);
            }
        });
        using HttpClient client = harness.CreateProxiedClient();

        long startedAt = Stopwatch.GetTimestamp();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri(harness.DestinationUri, "/events"), HttpCompletionOption.ResponseHeadersRead);

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using StreamReader reader = new(stream);

        string? first = await reader.ReadLineAsync();
        double firstMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        await reader.ReadToEndAsync();
        double totalMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        Assert.IsFalse(offered, "An event stream was buffered.");
        Assert.AreEqual("data: event-0", first);
        Assert.IsLessThan(totalMs / 2, firstMs, $"The first event arrived after {firstMs:0}ms of {totalMs:0}ms, so the stream was held.");
    }

    /// <summary>A body past the limit streams through, and arrives whole behind the bytes that
    /// were read while measuring it.</summary>
    [TestMethod]
    public async Task Response_Over_The_Limit_Is_Forwarded_Whole_And_Reported()
    {
        // Deliberately not a multiple of the read buffer, so a prefix served twice or not at all
        // shifts the whole body rather than landing on a boundary.
        string payload = new('x', 20_001);

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            ReplacingResponse("""{"never":true}"""),
            new BodyLimits(4096),
            respond: Answering(payload, "text/plain"));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/big"));

        Assert.AreEqual(payload, await response.Content.ReadAsStringAsync());
        Assert.IsTrue(
            harness.Warnings.Any(warning => warning.Contains("4096", StringComparison.Ordinal)),
            $"Skipping the oversized response was not logged. Warnings: {string.Join(" / ", harness.Warnings)}");
    }

    /// <summary>
    /// A few kilobytes of gzip can expand without bound. The limit is applied to what comes out
    /// of the decompressor, not only to what arrived, and the body is still delivered.
    /// </summary>
    [TestMethod]
    public async Task Response_That_Expands_Past_The_Limit_Is_Forwarded_Untouched()
    {
        string payload = new('x', 200_000);

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            ReplacingResponse("""{"never":true}"""),
            new BodyLimits(4096),
            respond: Answering(payload, "text/plain", contentEncoding: "gzip"));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/bomb"));

        // Compressed it fits under the limit, so it was read; decompressed it does not, so it was
        // put back exactly as it arrived rather than truncated.
        Assert.AreEqual("gzip", string.Join(",", response.Content.Headers.ContentEncoding));

        using GZipStream decompressed = new(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using StreamReader reader = new(decompressed);

        Assert.AreEqual(payload, await reader.ReadToEndAsync());
        Assert.IsTrue(
            harness.Warnings.Any(warning => warning.Contains("expands past", StringComparison.Ordinal)),
            $"The expansion limit was not logged. Warnings: {string.Join(" / ", harness.Warnings)}");
    }

    /// <summary>A response with no body must not be given one.</summary>
    [TestMethod]
    [DataRow(204)]
    [DataRow(304)]
    public async Task Bodyless_Statuses_Are_Never_Offered(int status)
    {
        bool offered = false;
        DelegateMutationFactory mutation = new(onResponse: (_, _) =>
        {
            offered = true;
            return "{}"u8.ToArray();
        });

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(mutation, respond: (context, _) =>
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            return Task.CompletedTask;
        });
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/nothing"));

        Assert.AreEqual(status, (int)response.StatusCode);
        Assert.IsFalse(offered, $"A {status} was given a body.");
    }

    /// <summary>A HEAD response describes a body without sending one, so there is nothing to
    /// read and nothing to replace.</summary>
    [TestMethod]
    public async Task Head_Response_Is_Never_Offered()
    {
        bool offered = false;
        DelegateMutationFactory mutation = new(onResponse: (_, _) =>
        {
            offered = true;
            return "{}"u8.ToArray();
        });

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            mutation,
            respond: Answering(OriginalBody));
        using HttpClient client = harness.CreateProxiedClient();

        using HttpRequestMessage request = new(HttpMethod.Head, new Uri(harness.DestinationUri, "/patients"));
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(offered, "A HEAD response was opened.");
    }

    /// <summary>A 206 carries a fragment described by a Content-Range that a rewrite would
    /// contradict.</summary>
    [TestMethod]
    public async Task Partial_Content_Is_Never_Offered()
    {
        bool offered = false;
        DelegateMutationFactory mutation = new(onResponse: (_, _) =>
        {
            offered = true;
            return "{}"u8.ToArray();
        });

        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(mutation, respond: async (context, _) =>
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentType = "text/plain";
            context.Response.Headers.ContentRange = "bytes 0-4/100";
            await context.Response.WriteAsync("hello", context.RequestAborted);
        });
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/fragment"));

        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.IsFalse(offered, "A partial response was opened.");
        Assert.AreEqual("hello", await response.Content.ReadAsStringAsync());
        Assert.AreEqual("bytes 0-4/100", string.Join(",", response.Content.Headers.GetValues(HeaderNames.ContentRange)));
    }

    /// <summary>
    /// The point of making the seam exchange-scoped: what was replaced on the way out can be put
    /// back on the way in, because one object saw both halves.
    /// </summary>
    [TestMethod]
    public async Task One_Mutation_Sees_Both_Halves_Of_An_Exchange()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            new TokenisingMutationFactory(),
            respond: async (context, body) =>
            {
                // The origin echoes the token it was sent, the way a real service would.
                string received = Encoding.UTF8.GetString(body);

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($$"""{"greeting":"Hallo {{received}}"}""", context.RequestAborted);
            });
        using HttpClient client = harness.CreateProxiedClient();

        using StringContent request = new("Hans Muster", Encoding.UTF8, "text/plain");
        using HttpResponseMessage response = await client.PostAsync(new Uri(harness.DestinationUri, "/greet"), request);

        // The origin never saw the real name, and the client never saw the token.
        RecordedRequest received = harness.RequireReceived();
        Assert.AreEqual("PERSON_1", Encoding.UTF8.GetString(received.Body));
        Assert.AreEqual("""{"greeting":"Hallo Hans Muster"}""", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The same exchange, as the dashboard hears about it: announced as treated once the body
    /// was scanned, then every step of the lifecycle in order, with the bodies at each step,
    /// and completed last.
    /// </summary>
    [TestMethod]
    public async Task A_Treated_Exchange_Is_Reported_Step_By_Step()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync(
            new TokenisingMutationFactory(),
            respond: async (context, body) =>
            {
                string received = Encoding.UTF8.GetString(body);

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($$"""{"greeting":"Hallo {{received}}"}""", context.RequestAborted);
            });
        using HttpClient client = harness.CreateProxiedClient();

        using StringContent request = new("Hans Muster", Encoding.UTF8, "text/plain");
        using HttpResponseMessage response = await client.PostAsync(new Uri(harness.DestinationUri, "/greet"), request);
        await response.Content.ReadAsStringAsync();
        await harness.NextCompletionAsync(TimeSpan.FromSeconds(10));

        string[] sequence = harness.Telemetry.Select(e => e.GetType().Name).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                nameof(RequestObserved),
                nameof(ExchangeOpened),
                nameof(DetectionCompleted),
                nameof(RedactionCompleted),
                nameof(UpstreamDispatched),
                nameof(UpstreamResponded),
                nameof(RehydrationCompleted),
                nameof(ExchangeDelivered),
                nameof(RequestCompleted),
            },
            sequence);

        RequestObserved observed = harness.Telemetry.OfType<RequestObserved>().Single();
        Assert.AreEqual(Treatment.Treated, observed.Treatment);
        Assert.AreEqual("1 identifier", observed.Reason);
        Assert.AreEqual("/greet", observed.Path);

        Assert.AreEqual("Hans Muster", harness.Telemetry.OfType<ExchangeOpened>().Single().RequestBody);
        Assert.AreEqual("PERSON_1", harness.Telemetry.OfType<RedactionCompleted>().Single().RedactedRequestBody);
        Assert.AreEqual(harness.DestinationUri.GetLeftPart(UriPartial.Authority), harness.Telemetry.OfType<UpstreamDispatched>().Single().Target);

        UpstreamResponded responded = harness.Telemetry.OfType<UpstreamResponded>().Single();
        Assert.AreEqual(200, responded.Status);
        Assert.AreEqual("""{"greeting":"Hallo PERSON_1"}""", responded.TokenizedResponseBody);

        RehydrationCompleted rehydrated = harness.Telemetry.OfType<RehydrationCompleted>().Single();
        Assert.AreEqual("""{"greeting":"Hallo Hans Muster"}""", rehydrated.ResponseBody);
        Assert.AreEqual(1, rehydrated.Restored);
    }

    /// <summary>A request the transform never read a body of is announced as passthrough,
    /// with the reason, and nothing about an exchange.</summary>
    [TestMethod]
    public async Task A_Request_Without_A_Body_Is_Reported_As_Passthrough()
    {
        await using ForwardingHarness harness = await ForwardingHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/plain"));
        await response.Content.ReadAsStringAsync();
        await harness.NextCompletionAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(
            new[] { nameof(RequestObserved), nameof(RequestCompleted) },
            harness.Telemetry.Select(e => e.GetType().Name).ToArray());

        RequestObserved observed = harness.Telemetry.OfType<RequestObserved>().Single();
        Assert.AreEqual(Treatment.Passthrough, observed.Treatment);
        Assert.AreEqual("no body", observed.Reason);
        Assert.IsNull(observed.ExchangeId);
    }

    /// <summary>
    /// Replaces one name with a token on the way out and puts it back on the way in, which is
    /// only possible because both calls land on the same object.
    /// </summary>
    private sealed class TokenisingMutationFactory : IBodyMutationFactory
    {
        public bool Rewrites => true;

        public IExchangeBodyMutation CreateForExchange(Uri destination, IExchangeObserver observer) => new Exchange(observer);

        private sealed class Exchange(IExchangeObserver observer) : IExchangeBodyMutation
        {
            private const string RealName = "Hans Muster";
            private const string Token = "PERSON_1";

            private bool _replaced;

            public ValueTask<byte[]?> MutateRequestAsync(
                ReadOnlyMemory<byte> body,
                BodyDescriptor descriptor,
                CancellationToken cancellationToken)
            {
                string text = descriptor.Encoding.GetString(body.Span);
                if (!text.Contains(RealName, StringComparison.Ordinal))
                    return ValueTask.FromResult<byte[]?>(null);

                _replaced = true;

                int start = text.IndexOf(RealName, StringComparison.Ordinal);
                observer.Detected(
                    [new DetectedEntity("e1", "PERSON", RealName, Token, start, start + RealName.Length, 0.9)],
                    new DetectionStats(1, 0, []));

                return ValueTask.FromResult<byte[]?>(
                    descriptor.Encoding.GetBytes(text.Replace(RealName, Token, StringComparison.Ordinal)));
            }

            public ValueTask<byte[]?> MutateResponseAsync(
                ReadOnlyMemory<byte> body,
                BodyDescriptor descriptor,
                CancellationToken cancellationToken)
            {
                string text = descriptor.Encoding.GetString(body.Span);
                if (!_replaced || !text.Contains(Token, StringComparison.Ordinal))
                    return ValueTask.FromResult<byte[]?>(null);

                string restored = text.Replace(Token, RealName, StringComparison.Ordinal);
                observer.Restored(restored, 1);

                return ValueTask.FromResult<byte[]?>(descriptor.Encoding.GetBytes(restored));
            }
        }
    }
}
