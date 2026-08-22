namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>
/// Translates the PII service's character offsets into indices into the .NET string they were
/// reported for.
///
/// The two sides do not count the same thing. Python counts one character per code point, so
/// an emoji, a rare CJK ideograph or a musical symbol is one; .NET counts UTF-16 code units,
/// where each of those is a surrogate pair and therefore two. Every offset the service reports
/// after the first such character is short by one per pair, which silently moves a replacement
/// off the text it was meant to cover -- and, once the drift exceeds the gap between two
/// findings, tears the slicing apart entirely.
///
/// Text without surrogates -- which is nearly all of it -- maps one to one and is recognised as
/// such in a single scan, so the map costs nothing to consult and nothing to build.
/// </summary>
sealed class CodePointOffsetMap
{
    /// <summary>For text that is all-BMP: the offsets already are the indices.</summary>
    private static readonly CodePointOffsetMap Identity = new(null);

    /// <summary>Index into the string for each code point, plus one entry for its end.</summary>
    private readonly int[]? indices;

    private CodePointOffsetMap(int[]? indices) => this.indices = indices;

    public static CodePointOffsetMap For(string text)
    {
        if (text.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') < 0)
            return Identity;

        int[] map = new int[text.Length + 1];
        int codePoints = 0;

        for (int index = 0; index < text.Length; codePoints++)
        {
            map[codePoints] = index;
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? 2
                : 1;
        }

        map[codePoints] = text.Length;
        Array.Resize(ref map, codePoints + 1);

        return new CodePointOffsetMap(map);
    }

    /// <summary>
    /// The index into the string, or -1 for an offset that lies past its end -- which a
    /// service that analysed different text than it was handed can report.
    /// </summary>
    public int ToStringIndex(int codePointOffset)
    {
        if (codePointOffset < 0)
            return -1;

        if (indices is null)
            return codePointOffset;

        return codePointOffset < indices.Length ? indices[codePointOffset] : -1;
    }
}
