using System.Text;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// One rewrite of a request body on its way out of the proxy.
///
/// Bytes in, bytes out, and nothing else: the encoding is named in
/// <see cref="RequestBodyDescriptor.Encoding"/> rather than guessed from a string overload,
/// and the framing headers are corrected by <see cref="ForwardProxyTransformer"/> from the
/// length of what is returned. An implementation therefore cannot make the outgoing request
/// inconsistent by forgetting a header.
///
/// Implementations are resolved once and called concurrently by every proxied request, so
/// they have to be stateless or thread-safe. A reusable <see cref="StringBuilder"/> or
/// buffer field held across calls interleaves two clients' bodies, under load, in
/// production, and nowhere else.
///
/// Adding a mutation:
/// 1. Implement this interface next to <see cref="PassthroughBodyMutation"/>.
/// 2. Replace the registration in <see cref="Registrar.AddForwardProxyServices"/>.
/// 3. Decide, in the implementation, what happens to a body it cannot parse. Returning null
///    forwards it unchanged; throwing fails the request with 502 and sends nothing. Both are
///    legitimate; neither may be left to an exception nobody meant to throw.
/// </summary>
interface IRequestBodyMutation
{
    /// <summary>
    /// The bytes to send in place of <paramref name="body"/>, or null to forward
    /// <paramref name="body"/> exactly as it arrived.
    ///
    /// Null is not merely the cheap path. It is what keeps every header the client sent --
    /// <c>Content-Encoding</c>, <c>Content-MD5</c>, any digest -- because those headers still
    /// describe these bytes truthfully. Returning a copy of the input instead would drop
    /// them, since the transform has no way to tell a copy from a rewrite.
    ///
    /// <paramref name="body"/> is read-only on purpose. A mutation that edited it in place
    /// and reported no change would leave the request describing bytes that are no longer
    /// there.
    /// </summary>
    ValueTask<byte[]?> MutateAsync(
        ReadOnlyMemory<byte> body,
        RequestBodyDescriptor descriptor,
        CancellationToken cancellationToken);
}

/// <summary>What a mutation is told about the body it is handed.</summary>
/// <param name="Destination">Where the request is going. Only <see cref="Uri.Host"/> is safe
/// to log; the path and the query routinely carry identifiers.</param>
/// <param name="ContentType">The client's <c>Content-Type</c> verbatim, or null when it sent
/// none. The transform never rewrites it, so a mutation that changes the media type has
/// nowhere to say so, by design.</param>
/// <param name="Encoding">The charset named in <paramref name="ContentType"/>, or UTF-8 when
/// it named none or named one this runtime does not carry. The substitution is logged, so a
/// mutation that decodes text is never quietly handed the wrong alphabet without a trace.</param>
sealed record RequestBodyDescriptor(Uri Destination, string? ContentType, Encoding Encoding);
