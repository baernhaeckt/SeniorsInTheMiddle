using System.Buffers;
using System.Text;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

using Yarp.ReverseProxy.Forwarder;

using MediaTypeHeaderValue = System.Net.Http.Headers.MediaTypeHeaderValue;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Shapes the request that leaves the proxy: the destination it is sent to, and the body it
/// carries.
///
/// The body is rewritten on <see cref="HttpContext.Request"/> and not on the outgoing
/// <see cref="HttpRequestMessage"/>. That is not a preference. YARP assigns its own streaming
/// content before this runs and refuses any replacement -- "Replacing the YARP outgoing
/// request HttpContent is not supported. You should configure the HttpContext.Request
/// instead." -- and the refusal is reported as a failed request creation and answered with
/// 502, so it reads like an unreachable destination rather than a bug in here.
///
/// Rewriting the incoming request instead is the better half of the bargain anyway. YARP
/// streams whatever <c>HttpContext.Request.Body</c> holds at the moment it sends, and it
/// builds the outgoing headers by copying <c>HttpContext.Request.Headers</c>. Correcting
/// both before <see cref="HttpTransformer.TransformRequestAsync"/> copies them makes the
/// framing right by construction: there is no second set of headers that can drift out of
/// step with the bytes.
///
/// It costs one thing, and only one. The body is now read here, before the destination
/// connection exists, so Kestrel answers a client's <c>Expect: 100-continue</c> as soon as
/// buffering starts, instead of leaving the destination to decline the upload first.
/// Skipping the rewrite for those requests would fix that and hand every client a
/// one-header way to opt out of inspection, which is the worse trade for a proxy whose job
/// is to look.
/// </summary>
sealed class ForwardProxyTransformer(
    Uri destination,
    IRequestBodyMutation mutation,
    RequestBodyLimits limits,
    ILogger<ForwardProxyTransformer> logger) : HttpTransformer
{
    /// <summary>
    /// What marks a header as carrying a signature computed over the payload. Nearly every
    /// vendor spells its webhook signature header with one of these in the name.
    ///
    /// Matching on a substring rather than on a list of names is deliberate. An unrecognised
    /// signature header means a rewritten body and a destination answering 401 with no
    /// explanation; an over-eager match only means one body goes uninspected. The second is
    /// the mistake to make.
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
    /// Headers that describe the original bytes rather than the message. They are dropped
    /// once the body is no longer the body they were written for: a surviving
    /// <c>Content-Encoding: gzip</c> makes the destination inflate plain text and fail, and a
    /// surviving digest simply does not match.
    ///
    /// Dropping rather than recomputing is deliberate. Recomputing a digest would re-assert
    /// an integrity claim on the proxy's behalf that the client never made.
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
    /// Largest buffer reserved up front from a client's declared Content-Length. Past this
    /// the buffer grows from bytes actually received: the declared length is a claim, and
    /// reserving a megabyte on it lets a client that opens many connections and then dribbles
    /// pin that much memory per connection without ever sending a body.
    /// </summary>
    private const int MaxPreallocatedBytes = 64 * 1024;

    private const int ReadChunkBytes = 8192;

    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        // Before the base transform, which is what copies the headers this may have changed.
        await RewriteBodyAsync(httpContext, cancellationToken);

        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        proxyRequest.RequestUri = destination;

        // Cleared so it is recomputed from the destination. The client's Host names this
        // proxy, and name-based virtual hosts route on it.
        proxyRequest.Headers.Host = null;
    }

    /// <summary>
    /// Offers the body to the mutation and leaves the request describing whatever comes back.
    ///
    /// Every path out of here leaves <c>HttpContext.Request.Body</c> readable from its first
    /// unread byte, because YARP streams from it after this returns. Nothing is consumed and
    /// dropped.
    /// </summary>
    private async ValueTask RewriteBodyAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        HttpRequest request = httpContext.Request;

        // The server answers this definitively. A GET or a HEAD gets no content at all, and
        // giving it one would put a Content-Length or a Transfer-Encoding on a request that
        // must carry neither; servers that do not expect a body there answer 400.
        if (httpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true)
            return;

        if (BodySigningHeader(request) is string signedBy)
        {
            logger.LogWarning(
                "Body left uninspected for {Host}: {Header} signs the payload and a rewrite would invalidate it.",
                destination.Host,
                signedBy);

            return;
        }

        byte[] buffered = await ReadAtMostAsync(request, limits.MaxMutableBodyBytes, cancellationToken);

        if (buffered.Length > limits.MaxMutableBodyBytes)
        {
            logger.LogWarning(
                "Body left uninspected for {Host}: larger than the {Limit} byte rewrite limit.",
                destination.Host,
                limits.MaxMutableBodyBytes);

            // What was read cannot be put back, so the rest of the body is served behind it.
            // No header is touched, so the client's own framing still describes the stream.
            request.Body = new PrefixedStream(buffered, request.Body);

            return;
        }

        byte[]? mutated = await mutation.MutateAsync(
            buffered,
            new RequestBodyDescriptor(destination, request.ContentType, EncodingOf(request.ContentType)),
            cancellationToken);

        if (mutated is null)
        {
            // Nothing was changed, so every header the client sent still describes these
            // bytes -- Content-Encoding and any digest included -- and none are disturbed.
            request.Body = new MemoryStream(buffered, writable: false);

            return;
        }

        request.Body = new MemoryStream(mutated, writable: false);

        foreach (string header in BodyDescribingHeaders)
        {
            request.Headers.Remove(header);
        }

        // Goes with them: the base transform drops Content-Length from the outgoing request
        // whenever the incoming one carried both framings, so a leftover Transfer-Encoding
        // would send the rewritten body with no framing at all.
        request.Headers.Remove(HeaderNames.TransferEncoding);

        // Last, and from the bytes rather than from anything the client claimed. Assigning
        // this writes the Content-Length header the base transform then copies, and an
        // explicit length is what decides the outgoing request is not sent chunked.
        request.ContentLength = mutated.Length;
    }

    /// <summary>
    /// The header that signs the body, or null when nothing does.
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
    /// The encoding the request declares, so a mutation that decodes text works from a named
    /// alphabet rather than a hopeful one.
    ///
    /// Input:  "application/json; charset=iso-8859-1" -> Latin1
    /// Input:  "application/json"                     -> UTF-8
    /// Input:  "text/plain; charset=windows-1252"     -> UTF-8, logged
    ///
    /// Only the UTF family, ASCII and Latin-1 are built into this runtime. Anything else, a
    /// legacy Windows code page included, needs System.Text.Encoding.CodePages registered at
    /// startup; until it is, the substitution says so rather than quietly handing a mutation
    /// the wrong alphabet.
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
                "Reading the body for {Host} as UTF-8: the declared charset {Charset} is not available to this runtime.",
                destination.Host,
                charset);

            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// Reads up to <paramref name="limit"/> + 1 bytes, so the caller can tell a body that fits
    /// from one that does not by length alone.
    ///
    /// The arithmetic is in long on purpose: <paramref name="limit"/> is configured, and
    /// "limit + 1" on a large one wraps negative and takes the request down with an argument
    /// out of range rather than a body out of range.
    /// </summary>
    private static async Task<byte[]> ReadAtMostAsync(
        HttpRequest request,
        int limit,
        CancellationToken cancellationToken)
    {
        long declared = request.ContentLength ?? 0;
        int capacity = (int)Math.Clamp(declared, 0, Math.Min(limit, MaxPreallocatedBytes));

        using MemoryStream buffer = new(capacity);
        byte[] chunk = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);

        try
        {
            while (buffer.Length <= limit)
            {
                long room = Math.Min(chunk.Length, (long)limit + 1 - buffer.Length);
                int read = await request.Body.ReadAsync(chunk.AsMemory(0, (int)room), cancellationToken);
                if (read == 0)
                    break;

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        // ToArray rather than GetBuffer: the mutation is handed exactly the body, and a
        // buffer that grew by doubling would otherwise carry a tail of zeroes into it.
        return buffer.ToArray();
    }
}
