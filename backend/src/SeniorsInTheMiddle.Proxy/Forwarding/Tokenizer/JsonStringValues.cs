using System.Text;
using System.Text.Json;

namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>
/// One string value of a JSON document: where its raw text sits in the document (between, not
/// including, the quotes) and what it decodes to.
/// </summary>
/// <param name="RawStart">Index into the document of the first char after the opening quote.</param>
/// <param name="RawLength">Chars up to the closing quote, escapes included.</param>
/// <param name="Value">The decoded value.</param>
/// <param name="Path">Where in the document it sits, as dotted keys: <c>customer.name</c>.
/// Array elements share their parent's path.</param>
sealed record JsonStringValue(int RawStart, int RawLength, string Value, string Path)
{
    public int RawEnd => RawStart + RawLength;

    /// <summary>Whether the raw text is the value verbatim, i.e. carries no escape sequence.</summary>
    public bool IsVerbatim => RawLength == Value.Length;

    /// <summary>
    /// Index into the raw text for each index into the decoded value, plus one for its end.
    ///
    /// A <c>\n</c> is two raw chars for one decoded, a <c>ä</c> six for one; a surrogate
    /// pair written as two <c>\u</c> escapes is twelve for two. So the map advances one decoded
    /// char per escape sequence, which is the one thing every JSON escape has in common.
    /// </summary>
    public int[] RawIndices(string document)
    {
        int[] map = new int[Value.Length + 1];
        int decoded = 0;

        for (int raw = 0; raw < RawLength && decoded < Value.Length; decoded++)
        {
            map[decoded] = raw;

            if (document[RawStart + raw] == '\\')
                raw += raw + 1 < RawLength && document[RawStart + raw + 1] == 'u' ? 6 : 2;
            else
                raw++;
        }

        map[Value.Length] = RawLength;

        return map;
    }
}

/// <summary>
/// Finds the string values of a JSON document, so that only what a person could have typed is
/// analysed -- not the keys, braces and quotes around it, which a named-entity model reads as
/// text and, given enough of them, confidently calls a name.
/// </summary>
static class JsonStringValues
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// The string values in document order, or null when <paramref name="document"/> is not
    /// JSON after all -- a declared content type is a claim, not a guarantee.
    /// </summary>
    public static IReadOnlyList<JsonStringValue>? Locate(string document)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(document);
        List<(int ByteStart, int ByteLength, string Value, string Path)> found = [];

        try
        {
            Utf8JsonReader reader = new(bytes, ReaderOptions);

            // The key of each enclosing object, innermost last, and the key the next value
            // belongs to. Arrays add nothing: their elements sit under the array's own key.
            Stack<string> enclosing = new();
            string current = string.Empty;

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        current = reader.GetString() ?? string.Empty;
                        break;

                    case JsonTokenType.StartObject:
                        enclosing.Push(current);
                        current = string.Empty;
                        break;

                    case JsonTokenType.EndObject:
                        current = enclosing.Pop();
                        break;

                    case JsonTokenType.String:
                        // TokenStartIndex is the opening quote; ValueSpan is the raw text
                        // between the quotes, escapes and all.
                        found.Add((
                            (int)reader.TokenStartIndex + 1,
                            reader.ValueSpan.Length,
                            reader.GetString() ?? string.Empty,
                            PathOf(enclosing, current)));
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return ToCharOffsets(document, found);
    }

    /// <summary>
    /// The reader counts UTF-8 bytes; the document is a string. One walk over it translates the
    /// (ascending) byte offsets into char indices.
    /// </summary>
    private static List<JsonStringValue> ToCharOffsets(string document, List<(int ByteStart, int ByteLength, string Value, string Path)> found)
    {
        List<JsonStringValue> values = new(found.Count);
        int charIndex = 0;
        int byteIndex = 0;

        foreach ((int byteStart, int byteLength, string value, string path) in found)
        {
            int charStart = Advance(document, ref charIndex, ref byteIndex, byteStart);
            int charEnd = Advance(document, ref charIndex, ref byteIndex, byteStart + byteLength);

            values.Add(new JsonStringValue(charStart, charEnd - charStart, value, path));
        }

        return values;
    }

    /// <summary>The enclosing keys outermost first, then the value's own, joined with dots.
    /// The stack enumerates innermost first, and the root object contributes an empty key.</summary>
    private static string PathOf(Stack<string> enclosing, string current)
    {
        IEnumerable<string> keys = enclosing.Reverse().Append(current).Where(key => key.Length > 0);

        return string.Join('.', keys);
    }

    private static int Advance(string document, ref int charIndex, ref int byteIndex, int toByte)
    {
        while (byteIndex < toByte && charIndex < document.Length)
        {
            char c = document[charIndex];

            if (char.IsHighSurrogate(c) && charIndex + 1 < document.Length && char.IsLowSurrogate(document[charIndex + 1]))
            {
                byteIndex += 4;
                charIndex += 2;
            }
            else
            {
                byteIndex += c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
                charIndex++;
            }
        }

        return charIndex;
    }
}
