using System.Text;

namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>
/// Writes a value the way it would appear inside a JSON string literal.
///
/// This is deliberately not <see cref="System.Text.Json.JsonEncodedText"/>. Both halves of the
/// rewrite have to agree character for character -- what the anonymizer splices into a request
/// has to be what the restore looks for in the response -- and the framework encoders are tuned
/// for HTML safety rather than for matching another writer's output. The default one escapes
/// <c>&amp;</c>, <c>'</c> and <c>&lt;</c>, which no other JSON writer does, so a stand-in
/// containing an apostrophe would be searched for in a spelling no origin ever sends.
///
/// The two spellings here are the two a real response actually arrives in.
/// </summary>
static class JsonText
{
    private const string HexDigits = "0123456789abcdef";

    /// <summary>
    /// <paramref name="value"/> as the contents of a JSON string, without the quotes.
    ///
    /// <paramref name="asciiOnly"/> false is what a JavaScript, Go or .NET writer produces:
    /// only what JSON requires -- the quote, the backslash, and the control characters -- with
    /// everything else left as the character it is.
    ///
    /// True is what Python's <c>json.dumps</c> produces by default (<c>ensure_ascii=True</c>):
    /// the same, plus every character above U+007F as a <c>\uXXXX</c> escape, astral characters
    /// as the two escapes of their surrogate pair. Lowercase hex, as both write it.
    ///
    /// Input:  René Bauer, false  -&gt; René Bauer
    /// Input:  René Bauer, true   -&gt; René Bauer
    /// Input:  Say "hi", either   -&gt; Say \"hi\"
    /// </summary>
    public static string Escape(string value, bool asciiOnly)
    {
        if (!NeedsEscaping(value, asciiOnly))
            return value;

        StringBuilder escaped = new(value.Length + 8);

        foreach (char character in value)
        {
            switch (character)
            {
                case '"':
                    escaped.Append("\\\"");
                    break;
                case '\\':
                    escaped.Append("\\\\");
                    break;
                case '\b':
                    escaped.Append("\\b");
                    break;
                case '\f':
                    escaped.Append("\\f");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                case '\r':
                    escaped.Append("\\r");
                    break;
                case '\t':
                    escaped.Append("\\t");
                    break;
                default:
                    if (character < 0x20 || (asciiOnly && character > 0x7F))
                        AppendUnicodeEscape(escaped, character);
                    else
                        escaped.Append(character);

                    break;
            }
        }

        return escaped.ToString();
    }

    /// <summary>Whether escaping would change anything, so the common case -- an ordinary name
    /// in an ordinary spelling -- returns the string it was handed.</summary>
    private static bool NeedsEscaping(string value, bool asciiOnly)
    {
        foreach (char character in value)
        {
            if (character is '"' or '\\' || character < 0x20 || (asciiOnly && character > 0x7F))
                return true;
        }

        return false;
    }

    private static void AppendUnicodeEscape(StringBuilder escaped, char character)
        => escaped
            .Append("\\u")
            .Append(HexDigits[(character >> 12) & 0xF])
            .Append(HexDigits[(character >> 8) & 0xF])
            .Append(HexDigits[(character >> 4) & 0xF])
            .Append(HexDigits[character & 0xF]);
}
