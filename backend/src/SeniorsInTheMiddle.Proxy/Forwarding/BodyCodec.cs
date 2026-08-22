using System.IO.Compression;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Undoing the <c>Content-Encoding</c> a body arrived under, so a mutation is always handed
/// plaintext.
///
/// Only what ships with the runtime is undone. A body under anything else is forwarded exactly
/// as it came: guessing at a compressed payload is worse than not looking at it, and the skip is
/// logged by the caller either way.
/// </summary>
static class BodyCodec
{
    /// <summary>
    /// A stream that undoes <paramref name="encoding"/>, or null when nothing here can.
    ///
    /// Input:  "gzip"     -> GZipStream
    /// Input:  "deflate"  -> ZLibStream, which is what RFC 9110 means by the name
    /// Input:  "br"       -> BrotliStream
    /// Input:  "identity" -> the stream unchanged
    /// Input:  "zstd"     -> null, no decompressor for it ships with .NET
    /// </summary>
    public static Stream? Decompressing(string encoding, Stream compressed)
    {
        string name = encoding.Trim();

        if (name.Length == 0 || Is(name, "identity"))
            return compressed;

        if (Is(name, "gzip") || Is(name, "x-gzip"))
            return new GZipStream(compressed, CompressionMode.Decompress);

        if (Is(name, "deflate"))
            return new ZLibStream(compressed, CompressionMode.Decompress);

        if (Is(name, "br"))
            return new BrotliStream(compressed, CompressionMode.Decompress);

        return null;
    }

    private static bool Is(string value, string name) => string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
}
