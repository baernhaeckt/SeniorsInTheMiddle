using System.Buffers;
using System.Net;
using System.Text;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

using Yarp.ReverseProxy.Forwarder;

using MediaTypeHeaderValue = System.Net.Http.Headers.MediaTypeHeaderValue;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Shapes both halves of one exchange: the request that leaves the proxy and the response that
/// comes back.
///
/// The request body is rewritten on <see cref="HttpContext.Request"/> and not on the outgoing
/// <see cref="HttpRequestMessage"/>. That is not a preference. YARP assigns its own streaming
/// content before this runs and refuses any replacement -- "Replacing the YARP outgoing request
/// HttpContent is not supported. You should configure the HttpContext.Request instead." -- and
/// the refusal is reported as a failed request creation answered with 502, so it reads like an
/// unreachable destination rather than a bug in here.
///
/// The response is the mirror image and, for once, the easier one. There is no such guard on
/// <see cref="HttpResponseMessage.Content"/>, so the body is replaced directly; and the base
/// transform copies the origin's headers onto the client response before this sees them, so the
/// ones that describe the old bytes are corrected afterwards rather than beforehand.
///
/// The request rewrite costs one thing. The body is read here, before the destination connection
/// exists, so Kestrel answers a client's <c>Expect: 100-continue</c> as soon as buffering starts
/// instead of leaving the destination to decline the upload first. Skipping the rewrite for those
/// requests would fix that and hand every client a one-header way to opt out of inspection, which
/// is the worse trade for a proxy whose job is to look.
/// </summary>
sealed class ForwardProxyTransformer(
    Uri destination,
    IExchangeBodyMutation mutation,
    BodyLimits limits,
    InspectionScope scope,
    ILogger<ForwardProxyTransformer> logger,
    ExchangeTrace trace) : HttpTransformer
{
    /// <summary>
    /// What marks a header as carrying a signature computed over the payload. Nearly every
    /// vendor spells its webhook signature header with one of these in the name.
    ///
    /// Matching on a substring rather than on a list of names is deliberate. An unrecognised
    /// signature header means a rewritten body and a destination answering 401 with no
    /// explanation; an over-eager match only means one body goes uninspected. The second is the
    /// mistake to make.
    /// </summary>
    private static readonly string[] SignatureHeaderMarkers = ["signature", "hmac"];

    /// <summary>
    /// Signature headers whose names <see cref="SignatureHeaderMarkers"/> does not catch.
    ///
    /// Adding a scheme: a header name here if its name says nothing about signing, or an
    /// authorization scheme in <see cref="SigningAuthorizationSchemes"/>.
    /// </summary>
    private static readonly string[] BodySignatureHeaders =
    [
        "x-amz-content-sha256",
        "PayPal-Transmission-Sig",
    ];

    /// <summary>Authorization schemes that sign the payload along with the headers.</summary>
    private static readonly string[] SigningAuthorizationSchemes =
    [
        "AWS4-HMAC-SHA256",
        "Signature",
        "Hmac",
    ];

    /// <summary>
    /// Headers that describe the original bytes rather than the message. They are dropped once
    /// the body is no longer the body they were written for: a surviving
    /// <c>Content-Encoding: gzip</c> makes the reader inflate plain text and fail, and a
    /// surviving digest simply does not match.
    ///
    /// Dropping rather than recomputing is deliberate. Recomputing a digest would re-assert an
    /// integrity claim on the proxy's behalf that neither end ever made.
    /// </summary>
    private static readonly string[] BodyDescribingHeaders =
    [
        HeaderNames.ContentEncoding,
        HeaderNames.ContentMD5,
        HeaderNames.ContentRange,
        "Content-Digest",
        "Repr-Digest",
        "Digest",
    ];

    /// <summary>
    /// Largest buffer reserved up front from a declared Content-Length. Past this the buffer
    /// grows from bytes actually received: the declared length is a claim, and reserving a
    /// megabyte on it lets a peer that opens many connections and then dribbles pin that much
    /// memory per connection without ever sending a body.
    /// </summary>
    private const int MaxPreallocatedBytes = 64 * 1024;

    /// <summary>Smallest buffer reserved, so a body with no declared length is not read a
    /// handful of bytes at a time.</summary>
    private const int MinBufferBytes = 8192;

    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        // Before the base transform, which is what copies the headers this may have changed.
        await RewriteRequestBodyAsync(httpContext, cancellationToken);

        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        proxyRequest.RequestUri = destination;

        // Cleared so it is recomputed from the destination. The client's Host names this proxy,
        // and name-based virtual hosts route on it.
        proxyRequest.Headers.Host = null;

        trace.Dispatched(destination.GetLeftPart(UriPartial.Authority), httpContext.Request.ContentLength ?? 0);
    }

    public override async ValueTask<bool> TransformResponseAsync(
        HttpContext httpContext,
        HttpResponseMessage? proxyResponse,
        CancellationToken cancellationToken)
    {
        // After the base transform, which is what puts the origin's headers on the client
        // response in the first place.
        bool shouldProxy = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);

        // The status alone, now; the body follows from the rewrite if there is one to read.
        if (proxyResponse is not null)
            trace.Responded((int)proxyResponse.StatusCode, string.Empty);

        if (shouldProxy && proxyResponse?.Content is not null)
            await RewriteResponseBodyAsync(httpContext, proxyResponse, cancellationToken);

        return shouldProxy;
    }

    /// <summary>
    /// Offers the request body to the mutation and leaves the request describing whatever comes
    /// back.
    ///
    /// Every path out of here leaves <c>HttpContext.Request.Body</c> readable from its first
    /// unread byte, because YARP streams from it after this returns. Nothing is consumed and
    /// dropped.
    /// </summary>
    private async ValueTask RewriteRequestBodyAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        HttpRequest request = httpContext.Request;

        // Rewriting turned off. Nothing is read, so the body reaches the destination exactly
        // as it arrived -- and, since no byte was taken off it, without the "left uninspected"
        // warning that measuring against a limit of zero would produce on every single
        // request. A setting that asks for a no-op gets a silent one.
        if (limits.MaxMutableBodyBytes == 0)
        {
            trace.Passthrough("rewriting disabled");

            return;
        }

        // The server answers this definitively. A GET or a HEAD gets no content at all, and
        // giving it one would put a Content-Length or a Transfer-Encoding on a request that must
        // carry neither; servers that do not expect a body there answer 400.
        if (httpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true)
        {
            trace.Passthrough("no body");

            return;
        }

        if (IsBotManagement())
        {
            trace.Passthrough("bot-management challenge");

            return;
        }

        if (!scope.Allows(destination))
        {
            trace.Passthrough("outside the inspected paths for this host");

            return;
        }

        if (BodySigningHeader(request) is string signedBy)
        {
            logger.LogWarning(
                "Request body left uninspected for {Host}: {Header} signs the payload and a rewrite would invalidate it.",
                destination.Host,
                signedBy);

            trace.Passthrough($"signed payload ({signedBy})");

            return;
        }

        byte[] buffered = await ReadAtMostAsync(
            request.Body,
            limits.MaxMutableBodyBytes,
            request.ContentLength ?? 0,
            cancellationToken);

        if (buffered.Length > limits.MaxMutableBodyBytes)
        {
            logger.LogWarning(
                "Request body left uninspected for {Host}: larger than the {Limit} byte rewrite limit.",
                destination.Host,
                limits.MaxMutableBodyBytes);

            // What was read cannot be put back, so the rest of the body is served behind it. No
            // header is touched, so the client's own framing still describes the stream. Kestrel
            // owns the body stream, so this must not close it.
            request.Body = new PrefixedStream(buffered, request.Body, leaveRestOpen: true);

            trace.Passthrough($"larger than {limits.MaxMutableBodyBytes} bytes");

            return;
        }

        BodyDescriptor descriptor = new(request.ContentType, EncodingOf(request.ContentType));
        trace.BodyBuffered(buffered, descriptor);

        byte[]? mutated;
        try
        {
            mutated = await mutation.MutateRequestAsync(buffered, descriptor, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately fatal. A mutation that threw did not finish deciding what in this body
            // had to be hidden, so forwarding the bytes it was handed would send exactly what it
            // exists to withhold. YARP answers the client 502, and the reason is here rather than
            // only in its RequestCreation warning.
            logger.LogError(
                ex,
                "Request body for {Host} was not rewritten and the request was not forwarded: the mutation failed over {Bytes} bytes of {ContentType}.",
                destination.Host,
                buffered.Length,
                request.ContentType ?? "no declared content type");

            trace.RequestRefused(ex);

            throw new BodyMutationException(
                $"The request body for {destination.Host} could not be rewritten, so it was not forwarded.",
                ex);
        }

        trace.RequestRewritten(mutated, descriptor);

        if (mutated is null)
        {
            // Nothing was changed, so every header the client sent still describes these bytes --
            // Content-Encoding and any digest included -- and none are disturbed.
            request.Body = new MemoryStream(buffered, writable: false);

            return;
        }

        request.Body = new MemoryStream(mutated, writable: false);

        foreach (string header in BodyDescribingHeaders)
        {
            request.Headers.Remove(header);
        }

        // Goes with them: the base transform drops Content-Length from the outgoing request
        // whenever the incoming one carried both framings, so a leftover Transfer-Encoding would
        // send the rewritten body with no framing at all.
        request.Headers.Remove(HeaderNames.TransferEncoding);

        // Last, and from the bytes rather than from anything the client claimed. Assigning this
        // writes the Content-Length header the base transform then copies, and an explicit length
        // is what decides the outgoing request is not sent chunked.
        request.ContentLength = mutated.Length;
    }

    /// <summary>
    /// Offers the response body to the mutation and leaves the client response describing
    /// whatever comes back.
    ///
    /// Half of this is about what not to touch. A request body is a finite document by the time
    /// it arrives; a response body may be a protocol upgrade, an event stream that never ends, or
    /// a fragment of something larger, and buffering any of those is a hang rather than a mistake
    /// anyone sees in a log.
    ///
    /// The other half is that the bytes on the wire and the bytes a mutation can read are not the
    /// same bytes. Both are held: the encoded ones because they are the only thing that still
    /// matches the headers already copied to the client, and the decoded ones because that is
    /// what a mutation works on. Every path from the first read onwards puts one or the other
    /// back, because by then the origin's stream is spent and there is nothing else left to send.
    /// </summary>
    private async ValueTask RewriteResponseBodyAsync(
        HttpContext httpContext,
        HttpResponseMessage proxyResponse,
        CancellationToken cancellationToken)
    {
        if (!MayReadBody(httpContext, proxyResponse))
            return;

        if (IsBotManagement() || !scope.Allows(destination))
            return;

        MediaTypeHeaderValue? contentType = proxyResponse.Content.Headers.ContentType;
        if (!IsInspectable(contentType?.MediaType))
            return;

        // One encoding this runtime can undo, or none at all. A chain of them is rare enough that
        // reading it is not worth the ways it can go wrong.
        string[] encodings = [.. proxyResponse.Content.Headers.ContentEncoding];
        if (encodings.Length > 1)
        {
            logger.LogWarning(
                "Response body left uninspected for {Host}: stacked content encodings {Encodings}.",
                destination.Host,
                string.Join(", ", encodings));

            return;
        }

        Stream origin = await proxyResponse.Content.ReadAsStreamAsync(cancellationToken);
        byte[] encoded = await ReadAtMostAsync(
            origin,
            limits.MaxMutableBodyBytes,
            proxyResponse.Content.Headers.ContentLength ?? 0,
            cancellationToken);

        if (encoded.Length > limits.MaxMutableBodyBytes)
        {
            logger.LogWarning(
                "Response body left uninspected for {Host}: larger than the {Limit} byte rewrite limit.",
                destination.Host,
                limits.MaxMutableBodyBytes);

            // What was read cannot be put back, so the rest is served behind it. The handler owns
            // this stream and a pooled connection stays out of circulation until it is closed.
            Replace(
                proxyResponse,
                new StreamContent(new PrefixedStream(encoded, origin, leaveRestOpen: false)),
                disposeReplaced: false);

            return;
        }

        byte[]? plain = Decoded(encodings, encoded);
        if (plain is null)
        {
            // Unreadable, but harmless: the encoded bytes are exactly what the headers on the
            // client response already promise.
            Replace(proxyResponse, new ByteArrayContent(encoded));

            return;
        }

        BodyDescriptor descriptor = new(contentType?.ToString(), EncodingOf(contentType?.ToString()));
        trace.Responded((int)proxyResponse.StatusCode, string.Empty);
        trace.ResponseBuffered(plain, descriptor);

        byte[]? mutated;
        try
        {
            mutated = await mutation.MutateResponseAsync(plain, descriptor, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not fatal, unlike the request side, and for the reason that decides both: this
            // direction restores what the proxy hid rather than hiding anything, so a failure
            // costs the client a body still carrying stand-in values -- not a disclosure. The
            // origin's own bytes are what the headers already sent to the client describe.
            logger.LogError(
                ex,
                "Response body from {Host} was not rewritten: the mutation failed over {Bytes} bytes of {ContentType}.",
                destination.Host,
                plain.Length,
                contentType?.MediaType ?? "no declared content type");

            Replace(proxyResponse, new ByteArrayContent(encoded));

            return;
        }

        if (mutated is null)
        {
            // Nothing was changed, so the response goes out exactly as it arrived, still encoded
            // and still described by the origin's own headers.
            Replace(proxyResponse, new ByteArrayContent(encoded));

            return;
        }

        Replace(proxyResponse, new ByteArrayContent(mutated));
        DescribeRewrittenBody(httpContext, mutated.Length);
    }

    /// <summary>
    /// The body as a mutation should see it, or null when this one cannot be read.
    ///
    /// Decoding happens from memory rather than off the wire so that a body which turns out to be
    /// unreadable, malformed, or a small archive of something enormous can still be forwarded
    /// exactly as it came.
    /// </summary>
    private byte[]? Decoded(string[] encodings, byte[] encoded)
    {
        if (encodings.Length == 0)
            return encoded;

        string encoding = encodings[0];
        using MemoryStream source = new(encoded, writable: false);
        using Stream? decoder = BodyCodec.Decompressing(encoding, source);

        if (decoder is null)
        {
            logger.LogWarning(
                "Response body left uninspected for {Host}: no decompressor for {Encoding}.",
                destination.Host,
                encoding);

            return null;
        }

        byte[] plain;
        try
        {
            // Reading synchronously is deliberate: the source is a MemoryStream, so there is
            // nothing to await and an async decompressor would only add a state machine.
            plain = ReadAtMost(decoder, limits.MaxMutableBodyBytes);
        }
        catch (InvalidDataException exception)
        {
            // A body that does not match the encoding it claims is the origin's mistake. Passing
            // it on unread lets the client decide, which is better than failing the response.
            logger.LogWarning(
                exception,
                "Response body left uninspected for {Host}: it is not valid {Encoding}.",
                destination.Host,
                encoding);

            return null;
        }

        if (plain.Length <= limits.MaxMutableBodyBytes)
            return plain;

        logger.LogWarning(
            "Response body left uninspected for {Host}: {Encoding} expands past the {Limit} byte rewrite limit.",
            destination.Host,
            encoding,
            limits.MaxMutableBodyBytes);

        return null;
    }

    /// <summary>
    /// Whether this response has a body that can be held at all.
    ///
    /// The forwarder checks for a protocol upgrade only after this transform has run, so a 101
    /// arrives here with a live duplex stream where its body should be. Reading that one does not
    /// fail: it waits for the far end forever, and takes the WebSocket with it.
    /// </summary>
    private static bool MayReadBody(HttpContext httpContext, HttpResponseMessage proxyResponse)
    {
        if (proxyResponse.StatusCode == HttpStatusCode.SwitchingProtocols)
            return false;

        // A 206 carries a fragment described by a Content-Range that a rewrite would contradict,
        // and reassembling the whole is not this proxy's business.
        if (proxyResponse.StatusCode == HttpStatusCode.PartialContent)
            return false;

        // 1xx, 204, 205 and 304 are terminated by their headers and cannot carry content.
        if ((int)proxyResponse.StatusCode is (>= 100 and < 200) or 204 or 205 or 304)
            return false;

        // A HEAD response describes a body it does not send, so there is nothing here to read and
        // giving it one would be a protocol error rather than a redaction.
        return !HttpMethods.IsHead(httpContext.Request.Method);
    }

    /// <summary>
    /// Whether a media type is one worth opening.
    ///
    /// Most of what a browser fetches is stylesheets, scripts, fonts and images, which cannot
    /// carry a person's details and are expensive to hold; those are passed through unread. The
    /// list is what a mutation can actually work on as text.
    ///
    /// Input:  "application/json"        -> true
    /// Input:  "application/ld+json"     -> true
    /// Input:  "text/html"               -> true
    /// Input:  "text/event-stream"       -> false, it never ends
    /// Input:  "image/png"               -> false
    ///
    /// Adding a type: one entry here, and a mutation that knows what to do with it.
    /// </summary>
    private static bool IsInspectable(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
            return false;

        // Under text/ but endless, so it is the one exception that has to come first.
        if (mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return false;

        // Code, not content. Two reasons, and either alone would be enough.
        //
        // Nobody types a name into a stylesheet, so there is nothing here worth the scan. And a
        // script is the one body where a substitution is not merely wrong but load-bearing: a
        // stand-in spliced into minified JavaScript can change what the page computes, and a bot
        // challenge is exactly a script whose output is checked. Rewriting it turns a page that
        // would have loaded into one that fails a check it can never pass -- see IsBotManagement.
        if (IsScriptOrStyle(mediaType))
            return false;

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
               || mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase)
               || mediaType.EndsWith("xml", StringComparison.OrdinalIgnoreCase)
               || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a media type carries code rather than something a person wrote.
    ///
    /// Input:  "text/javascript"        -> true
    /// Input:  "application/javascript" -> true
    /// Input:  "text/css"               -> true
    /// Input:  "application/json"       -> false
    /// </summary>
    private static bool IsScriptOrStyle(string mediaType)
        => mediaType.EndsWith("javascript", StringComparison.OrdinalIgnoreCase)
           || mediaType.EndsWith("ecmascript", StringComparison.OrdinalIgnoreCase)
           || mediaType.Equals("text/css", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a path belongs to a bot-management challenge rather than to the site itself.
    ///
    /// Cloudflare serves the whole of one from <c>/cdn-cgi/</c> on the site's own origin: the
    /// script, the orchestrator, and the POST carrying the answer. The answer is a token computed
    /// over what the script saw, so a single byte changed anywhere in that exchange fails it, and
    /// the client is handed a fresh challenge and tries again -- the redirect loop, with a 400 on
    /// the submission underneath it.
    ///
    /// This is deliberately a path rule and not a host rule. The site around it stays intercepted,
    /// which is the point: on chatgpt.com the challenge under /cdn-cgi/ passes through untouched
    /// while /backend-api/conversation is still read and rewritten like any other body.
    /// </summary>
    private bool IsBotManagement()
        => destination.AbsolutePath.StartsWith("/cdn-cgi/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Swaps the body the forwarder will send, and disposes the one it replaces so the pooled
    /// connection behind it is released rather than waiting for a finalizer.
    ///
    /// <paramref name="disposeReplaced"/> is false in exactly one case: when the replacement is
    /// still reading from the stream the replaced content owns. Disposing it there closes that
    /// stream out from under the body being sent, and the client sees a response that ends early.
    /// The replacement takes over releasing the connection instead.
    /// </summary>
    private static void Replace(
        HttpResponseMessage proxyResponse,
        HttpContent replacement,
        bool disposeReplaced = true)
    {
        HttpContent replaced = proxyResponse.Content;
        proxyResponse.Content = replacement;

        if (disposeReplaced)
            replaced.Dispose();
    }

    /// <summary>
    /// Restates the client response for a body that is no longer the one the origin sent.
    ///
    /// Transfer-Encoding needs no attention here: the base transform never copies it to the
    /// client, so Kestrel frames the response from this length alone.
    /// </summary>
    private static void DescribeRewrittenBody(HttpContext httpContext, int length)
    {
        foreach (string header in BodyDescribingHeaders)
        {
            httpContext.Response.Headers.Remove(header);
        }

        httpContext.Response.ContentLength = length;
    }

    /// <summary>
    /// The header that signs the request body, or null when nothing does.
    ///
    /// Input:  "x-amz-content-sha256: e3b0c44..."                -> "x-amz-content-sha256"
    /// Input:  "X-Hub-Signature-256: sha256=7d38..."             -> "X-Hub-Signature-256"
    /// Input:  "Authorization: AWS4-HMAC-SHA256 Credential=..."  -> "Authorization"
    /// Input:  "Authorization: Bearer eyJ..."                    -> null
    /// </summary>
    private static string? BodySigningHeader(HttpRequest request)
    {
        foreach (string header in BodySignatureHeaders)
        {
            if (request.Headers.ContainsKey(header))
                return header;
        }

        foreach (KeyValuePair<string, StringValues> header in request.Headers)
        {
            foreach (string marker in SignatureHeaderMarkers)
            {
                if (header.Key.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return header.Key;
            }
        }

        string authorization = request.Headers.Authorization.ToString();
        foreach (string scheme in SigningAuthorizationSchemes)
        {
            if (authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return HeaderNames.Authorization;
        }

        return null;
    }

    /// <summary>
    /// The encoding a message declares, so a mutation that decodes text works from a named
    /// alphabet rather than a hopeful one.
    ///
    /// Input:  "application/json; charset=iso-8859-1" -> Latin1
    /// Input:  "application/json"                     -> UTF-8
    /// Input:  "text/plain; charset=windows-1252"     -> UTF-8, logged
    ///
    /// Only the UTF family, ASCII and Latin-1 are built into this runtime. Anything else, a
    /// legacy Windows code page included, needs System.Text.Encoding.CodePages registered at
    /// startup; until it is, the substitution says so rather than quietly handing a mutation the
    /// wrong alphabet.
    /// </summary>
    private Encoding EncodingOf(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? mediaType) ||
            string.IsNullOrWhiteSpace(mediaType.CharSet))
        {
            return Encoding.UTF8;
        }

        string charset = mediaType.CharSet.Trim('"');

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            logger.LogWarning(
                "Reading a body for {Host} as UTF-8: the declared charset {Charset} is not available to this runtime.",
                destination.Host,
                charset);

            return Encoding.UTF8;
        }
    }

    /// <summary>One past the limit, so a body that fits comes back shorter than what a body that
    /// does not fit comes back as, and the caller can tell them apart by length alone.
    ///
    /// The arithmetic is in long on purpose: the limit is configured, and "limit + 1" on a large
    /// one wraps negative and takes the exchange down with an argument out of range rather than a
    /// body out of range.</summary>
    private static int Ceiling(int limit) => (int)Math.Min((long)limit + 1, int.MaxValue);

    /// <summary>
    /// How much to reserve before the first read.
    ///
    /// A declared length is only a claim, so it caps the reservation rather than deciding it:
    /// reserving the whole ceiling on it would let a peer that opens many connections and then
    /// dribbles pin that much memory per connection without ever sending a body.
    /// </summary>
    private static int InitialCapacity(int ceiling, long declaredLength)
        => (int)Math.Min(
            Math.Max(declaredLength, MinBufferBytes),
            Math.Min(ceiling, MaxPreallocatedBytes));

    /// <summary>
    /// <paramref name="buffer"/>, grown once it is full, never past <paramref name="ceiling"/>.
    ///
    /// Doubling rather than jumping straight to the ceiling. The ceiling is the configured
    /// rewrite limit, not a measurement of this body: a 65 KB body under the 64 MB the setting
    /// allows would reserve 64 MB, and enough concurrent ordinary uploads would reserve it each,
    /// which is exactly what <see cref="MaxPreallocatedBytes"/> exists to prevent. Growth stays
    /// proportional to what has actually arrived, and every step comes from the pool rather than
    /// from a fresh large-object-heap allocation.
    /// </summary>
    private static byte[] Grown(byte[] buffer, int filled, int ceiling)
    {
        if (filled < buffer.Length)
            return buffer;

        byte[] grown = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)buffer.Length * 2, ceiling));
        buffer.AsSpan(0, filled).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(buffer);

        return grown;
    }

    /// <summary>
    /// The synchronous twin of <see cref="ReadAtMostAsync"/>, for decoding a buffer that is
    /// already in memory. Same "limit + 1" contract, so the caller can still tell a body that
    /// fits from one that does not.
    /// </summary>
    private static byte[] ReadAtMost(Stream body, int limit)
    {
        int ceiling = Ceiling(limit);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity(ceiling, declaredLength: 0));
        int filled = 0;

        try
        {
            while (filled < ceiling)
            {
                buffer = Grown(buffer, filled, ceiling);

                int read = body.Read(buffer, filled, Math.Min(buffer.Length, ceiling) - filled);
                if (read == 0)
                    break;

                filled += read;
            }

            return Exactly(buffer, filled);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads up to <paramref name="limit"/> + 1 bytes, so the caller can tell a body that fits
    /// from one that does not by length alone.
    ///
    /// Reads into pooled buffers rather than a MemoryStream, so the arrays a growing body passes
    /// through are recycled instead of being allocated afresh on the large object heap for every
    /// request -- see <see cref="Grown"/>.
    /// </summary>
    private static async Task<byte[]> ReadAtMostAsync(
        Stream body,
        int limit,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        int ceiling = Ceiling(limit);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity(ceiling, declaredLength));
        int filled = 0;

        try
        {
            while (filled < ceiling)
            {
                buffer = Grown(buffer, filled, ceiling);

                int read = await body.ReadAsync(
                    buffer.AsMemory(filled, Math.Min(buffer.Length, ceiling) - filled),
                    cancellationToken);

                if (read == 0)
                    break;

                filled += read;
            }

            return Exactly(buffer, filled);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// A copy of the first <paramref name="filled"/> bytes.
    ///
    /// A copy rather than the rented array itself: the mutation is handed exactly the body, and
    /// a pooled buffer is longer than what was read and goes back to the pool afterwards.
    /// </summary>
    private static byte[] Exactly(byte[] buffer, int filled) => buffer.AsSpan(0, filled).ToArray();
}
