using System.IO.Compression;
using System.Text;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins which Content-Encodings a body can be inspected under.
///
/// Two ways to get this wrong, and they fail in opposite directions. Failing to recognise an
/// encoding the runtime can undo means a body that quietly goes uninspected -- personal data
/// forwarded because it happened to arrive gzipped. Claiming to undo one that cannot be undone
/// hands the detector compressed bytes, and whatever it splices into them corrupts the body.
/// So an unknown encoding returns null rather than guessing, and the caller forwards it as it
/// came.
/// </summary>
[TestClass]
public class BodyCodecTests
{
    private const string Payload = "Grüezi Hans Meier, mail an hans@example.ch";

    private static byte[] Compress(string encoding, string text)
    {
        MemoryStream compressed = new();
        byte[] plain = Encoding.UTF8.GetBytes(text);

        using (Stream compressor = encoding switch
        {
            "gzip" => new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true),
            "deflate" => new ZLibStream(compressed, CompressionMode.Compress, leaveOpen: true),
            "br" => new BrotliStream(compressed, CompressionMode.Compress, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Not a codec this test compresses with."),
        })
        {
            compressor.Write(plain);
        }

        return compressed.ToArray();
    }

    private static string Decompress(string headerValue, byte[] body)
    {
        using MemoryStream source = new(body);
        using Stream? plain = BodyCodec.Decompressing(headerValue, source);

        Assert.IsNotNull(plain, $"No decompressor for '{headerValue}'.");

        using StreamReader reader = new(plain, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The three encodings a browser actually offers, round-tripped.</summary>
    [TestMethod]
    [DataRow("gzip", "gzip")]
    [DataRow("gzip", "GZIP")]
    [DataRow("gzip", "x-gzip")]
    [DataRow("gzip", " gzip ")]
    [DataRow("deflate", "deflate")]
    [DataRow("deflate", "Deflate")]
    [DataRow("br", "br")]
    [DataRow("br", "BR")]
    public void A_Compressed_Body_Comes_Back_As_Plain_Text(string codec, string headerValue)
    {
        Assert.AreEqual(Payload, Decompress(headerValue, Compress(codec, Payload)));
    }

    /// <summary>
    /// RFC 9110's "deflate" means zlib, not a raw deflate stream. Reading it as raw deflate
    /// is the classic mistake and fails on the two-byte zlib header.
    /// </summary>
    [TestMethod]
    public void Deflate_Means_Zlib()
    {
        MemoryStream raw = new();
        using (DeflateStream compressor = new(raw, CompressionMode.Compress, leaveOpen: true))
            compressor.Write(Encoding.UTF8.GetBytes(Payload));

        using MemoryStream source = new(raw.ToArray());
        using Stream? plain = BodyCodec.Decompressing("deflate", source);
        Assert.IsNotNull(plain);

        using StreamReader reader = new(plain, Encoding.UTF8);
        Assert.ThrowsExactly<InvalidDataException>(() => reader.ReadToEnd());
    }

    /// <summary>An absent or identity encoding hands the body straight through, not a copy of it.</summary>
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("identity")]
    [DataRow("Identity")]
    public void An_Uncompressed_Body_Is_The_Same_Stream(string headerValue)
    {
        using MemoryStream source = new(Encoding.UTF8.GetBytes(Payload));

        Assert.AreSame(source, BodyCodec.Decompressing(headerValue, source));
    }

    /// <summary>
    /// zstd is the realistic one: increasingly served, and nothing in the runtime undoes it.
    /// Returning null is what makes the caller forward the body untouched and log the skip.
    /// </summary>
    [TestMethod]
    [DataRow("zstd")]
    [DataRow("compress")]
    [DataRow("gzip, br")]
    [DataRow("snappy")]
    [DataRow("gzipp")]
    public void An_Encoding_Nothing_Here_Undoes_Returns_Null(string headerValue)
    {
        using MemoryStream source = new(Encoding.UTF8.GetBytes(Payload));

        Assert.IsNull(BodyCodec.Decompressing(headerValue, source));
    }

    /// <summary>
    /// A body that declares gzip but is not gzip is a real thing on the open web. It has to
    /// fail as it is read, not corrupt the text: the caller catches this and forwards the
    /// original bytes.
    /// </summary>
    [TestMethod]
    public void A_Body_That_Lies_About_Its_Encoding_Fails_On_Read()
    {
        using MemoryStream source = new(Encoding.UTF8.GetBytes(Payload));
        using Stream? plain = BodyCodec.Decompressing("gzip", source);
        Assert.IsNotNull(plain);

        using StreamReader reader = new(plain, Encoding.UTF8);
        Assert.ThrowsExactly<InvalidDataException>(() => reader.ReadToEnd());
    }

    /// <summary>An empty body is legal under any encoding and must not be treated as a fault.</summary>
    [TestMethod]
    public void An_Empty_Compressed_Body_Reads_As_Empty()
    {
        Assert.AreEqual(string.Empty, Decompress("gzip", Compress("gzip", string.Empty)));
    }

    /// <summary>
    /// The limit that matters is on the decompressed size, so the caller has to be able to
    /// see one. A few kilobytes expanding to megabytes is the shape of the attack.
    /// </summary>
    [TestMethod]
    public void A_Small_Compressed_Body_Can_Expand_A_Long_Way()
    {
        string repetitive = new('a', 4 * 1024 * 1024);
        byte[] compressed = Compress("gzip", repetitive);

        Assert.IsLessThan(64 * 1024, compressed.Length);
        Assert.HasCount(repetitive.Length, Decompress("gzip", compressed).ToCharArray());
    }
}
