namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// How much of a body the proxy is willing to hold while it is rewritten, read from the
/// <c>Proxy</c> configuration section. The same limit applies in both directions.
///
/// A forward proxy sees whatever the device sends and whatever the site sends back, uploads and
/// video included, and a body has to be buffered whole before it can be rewritten: the
/// replacement's length is not known until it exists, and a body sent without a length goes out
/// chunked, which several API gateways and older stacks refuse on requests. Buffering everything
/// would put an arbitrary download in memory, so the limit is the line between the two. Below it
/// a body is buffered and offered to the mutation; above it the bytes stream through untouched
/// and the skip is logged rather than left to be inferred from a body that was never inspected.
///
/// It bounds the decompressed size too, not just the bytes on the wire. A few kilobytes of gzip
/// can expand to gigabytes, and a proxy that decompresses without a ceiling is a proxy anyone
/// can stop with one response.
/// </summary>
/// <param name="MaxMutableBodyBytes">Largest body read into memory and offered to the mutation.
/// Zero turns rewriting off without removing it from the pipeline.</param>
sealed record BodyLimits(int MaxMutableBodyBytes)
{
    /// <summary>One megabyte: comfortably above JSON payloads and form posts, far below the
    /// transfers that must not be buffered.</summary>
    public const int DefaultMaxMutableBodyBytes = 1024 * 1024;

    /// <summary>
    /// The ceiling the setting is refused above.
    ///
    /// The limit is per concurrent exchange, so it multiplies by however many devices are
    /// talking at once. int.MaxValue is the obvious way to spell "no limit" and is exactly the
    /// value that turns one download into an OutOfMemoryException, which is why it is refused at
    /// startup rather than discovered in production.
    /// </summary>
    public const int MaxAllowedMutableBodyBytes = 64 * 1024 * 1024;

    public static BodyLimits From(IConfiguration configuration)
    {
        int maxMutableBodyBytes = configuration.GetValue("Proxy:MaxMutableBodyBytes", DefaultMaxMutableBodyBytes);

        if (maxMutableBodyBytes < 0)
        {
            throw new InvalidOperationException(
                $"Proxy:MaxMutableBodyBytes ({maxMutableBodyBytes}) cannot be negative. Use 0 to forward every body untouched.");
        }

        if (maxMutableBodyBytes > MaxAllowedMutableBodyBytes)
        {
            throw new InvalidOperationException(
                $"Proxy:MaxMutableBodyBytes ({maxMutableBodyBytes}) is above the {MaxAllowedMutableBodyBytes} byte ceiling. "
                + "The limit is held per concurrent exchange, so a larger value trades a rewritten body for the process.");
        }

        return new BodyLimits(maxMutableBodyBytes);
    }
}
