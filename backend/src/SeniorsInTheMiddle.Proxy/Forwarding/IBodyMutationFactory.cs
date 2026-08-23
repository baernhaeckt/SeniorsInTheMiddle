using System.Text;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Makes the rewrite for one client exchange.
///
/// A factory rather than one shared mutation because the two halves of an exchange are not
/// independent: replacing an identifier on the way out only works if the same value can be put
/// back on the way in, and the map between them belongs to the mutation this returns.
///
/// That map may outlive the exchange -- a chat client is answered in one request and draws the
/// answer from another -- which is why the client is named here. Anything a mutation keeps
/// beyond the exchange is that client's, and reaches no other.
///
/// Adding a mutation:
/// 1. Implement this and <see cref="IExchangeBodyMutation"/> next to
///    <see cref="PassthroughMutationFactory"/>.
/// 2. Replace the registration in <see cref="Registrar.AddForwardProxyServices"/>.
/// 3. Decide, in the implementation, what happens to a body it cannot parse. Returning null
///    forwards it unchanged; throwing fails the exchange. Both are legitimate; neither may be
///    left to an exception nobody meant to throw.
/// </summary>
interface IBodyMutationFactory
{
    /// <summary>
    /// Whether this mutation changes bodies at all. The dashboard's hello reports the policy
    /// from this rather than from a type check, so a new mutation cannot be announced as
    /// "observe-only" by accident.
    /// </summary>
    bool Rewrites { get; }

    /// <summary>
    /// The mutation for one request and the response it earns.
    ///
    /// The factory itself is resolved once and called concurrently, so it has to be stateless
    /// or thread-safe. What it returns is one exchange's, but whatever it shares with the
    /// exchanges before it -- see <see cref="ClientIdentity"/> -- is not.
    /// </summary>
    IExchangeBodyMutation CreateForExchange(ClientIdentity client, Uri destination, IExchangeObserver observer);
}

/// <summary>
/// What a mutation tells the telemetry about the exchange it is rewriting. Three facts and no
/// more: the mutation sees bytes, not HTTP, and the trace that implements this
/// (<see cref="ExchangeTrace"/>) owns everything about when and in which order they are
/// reported onwards. Every call is cheap and never throws.
/// </summary>
interface IExchangeObserver
{
    /// <summary>The body was read and not inspected, for <paramref name="reason"/> -- a media
    /// type the mutation does not read, say.</summary>
    void Passthrough(string reason);

    /// <summary>What was found in the request body and replaced. Offsets are indices into the
    /// decoded body text. An empty list is a body that was scanned and found clean.</summary>
    void Detected(IReadOnlyList<Telemetry.DetectedEntity> entities, DetectionStats stats);

    /// <summary>The response body is in hand and about to be offered to the mutation.</summary>
    void ResponseBuffered();

    /// <summary>The response body with the proxy's stand-ins put back, and how many were.</summary>
    void Restored(string responseBody, int restored);

    /// <summary>
    /// The request body as the mutation decoded it. Optional: a mutation that reads the body
    /// as text hands the text over so the trace does not decode the same bytes a second time.
    /// </summary>
    void RequestText(string text) { }

    /// <summary>The rewritten request body as text, for the same reason as <see cref="RequestText"/>.</summary>
    void RewrittenText(string text) { }

    /// <summary>The response body as text, before anything was put back.</summary>
    void ResponseText(string text) { }
}

/// <summary>What a scan cost and what it left out, beside the entities it produced.</summary>
/// <param name="ScannedMs">The detector's own time.</param>
/// <param name="Suppressed">Findings reported but not replaced on their own.</param>
/// <param name="NearMisses">Findings below the confidence threshold.</param>
sealed record DetectionStats(double ScannedMs, int Suppressed, IReadOnlyList<Telemetry.NearMiss> NearMisses)
{
    public static readonly DetectionStats None = new(0, 0, []);
}

/// <summary>For a mutation that has nothing to report, and for tests.</summary>
sealed class NullExchangeObserver : IExchangeObserver
{
    public static readonly NullExchangeObserver Instance = new();

    public void Passthrough(string reason) { }

    public void Detected(IReadOnlyList<Telemetry.DetectedEntity> entities, DetectionStats stats) { }

    public void ResponseBuffered() { }

    public void Restored(string responseBody, int restored) { }
}

/// <summary>
/// One exchange's rewrite, request and response.
///
/// Bytes in, bytes out, and nothing else: the encoding is named in
/// <see cref="BodyDescriptor.Encoding"/> rather than guessed, the body arrives decompressed
/// whatever the wire carried, and the framing headers are corrected by
/// <see cref="ForwardProxyTransformer"/> from the length of what comes back. An implementation
/// cannot leave the message inconsistent by forgetting a header.
///
/// One instance serves one exchange and the calls are ordered -- the request first, the
/// response after it -- so an implementation may keep whatever it needs between them in
/// ordinary fields.
/// </summary>
interface IExchangeBodyMutation
{
    /// <summary>
    /// The bytes to send in place of <paramref name="body"/>, or null to forward it exactly as
    /// it arrived.
    ///
    /// Null is not merely the cheap path. It is what keeps every header the client sent --
    /// <c>Content-Encoding</c>, <c>Content-MD5</c>, any digest -- because those still describe
    /// these bytes truthfully. Returning a copy of the input instead would drop them, since the
    /// transform cannot tell a copy from a rewrite.
    /// </summary>
    ValueTask<byte[]?> MutateRequestAsync(
        ReadOnlyMemory<byte> body,
        BodyDescriptor descriptor,
        CancellationToken cancellationToken);

    /// <summary>
    /// The same, for the response, and the place where anything replaced on the way out is put
    /// back.
    ///
    /// It is not called at all for a response that must not be held: a protocol upgrade, a
    /// partial body, or a media type nothing here can read. An event stream is not in that list
    /// any more -- see <see cref="CreateResponseStream"/>.
    /// </summary>
    ValueTask<byte[]?> MutateResponseAsync(
        ReadOnlyMemory<byte> body,
        BodyDescriptor descriptor,
        CancellationToken cancellationToken);

    /// <summary>
    /// The restore for a response that cannot be held, or null when this mutation has nothing to
    /// put back into one.
    ///
    /// An event stream is the body a chat backend answers with, and it is also the one body this
    /// proxy is not allowed to buffer: it ends when the conversation does, and holding it whole
    /// is not a slow response but no response at all. So it is rewritten as it goes, in whatever
    /// pieces the origin sends, which the whole-body call above never has to think about.
    ///
    /// Returning null leaves the stream untouched, which is what a mutation that has hidden
    /// nothing should do rather than paying to copy every byte through itself -- and is the
    /// default, so a mutation that only rewrites whole documents says nothing about streams and
    /// gets the behaviour it had before this existed.
    /// </summary>
    IExchangeStreamMutation? CreateResponseStream(BodyDescriptor descriptor) => null;
}

/// <summary>
/// A rewrite applied to a response as it arrives, for the bodies that must not be buffered.
///
/// The contract is text in, text out, and the difference from the whole-body call is entirely in
/// what it is allowed to keep: <see cref="Mutate"/> may return less than it was given, holding
/// the tail back until the next chunk says what it was the start of. <see cref="Flush"/> is
/// called exactly once, when the origin has nothing more to send, and returns whatever is still
/// held.
///
/// Calls are ordered and never concurrent -- one stream, read one chunk at a time.
/// </summary>
interface IExchangeStreamMutation
{
    /// <summary>The text to write in place of <paramref name="chunk"/>. May be empty: a chunk
    /// that is entirely the possible beginning of a stand-in is held, not dropped.</summary>
    string Mutate(string chunk);

    /// <summary>Whatever is still held back, once the origin has ended. Called once, and after
    /// it nothing else is.</summary>
    string Flush();
}

/// <summary>What a mutation is told about the body it is handed.</summary>
/// <param name="ContentType">The <c>Content-Type</c> verbatim, or null when none was sent. The
/// transform never rewrites it, so a mutation that changes the media type has nowhere to say
/// so, by design.</param>
/// <param name="Encoding">The charset named in <paramref name="ContentType"/>, or UTF-8 when it
/// named none or named one this runtime does not carry. The substitution is logged, so a
/// mutation is never quietly handed the wrong alphabet.</param>
sealed record BodyDescriptor(string? ContentType, Encoding Encoding);
