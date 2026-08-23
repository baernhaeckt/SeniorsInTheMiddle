using System.Diagnostics;
using System.Text;

using SeniorsInTheMiddle.Proxy.Telemetry;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// What the proxy knew about a request before it read the body. Collected once in
/// <see cref="ForwardProxy"/> and carried by <see cref="ExchangeTrace"/> until the request is
/// announced.
/// </summary>
sealed record RequestFacts(
    string ClientIp,
    string ClientLabel,
    string Method,
    TelemetryScheme Scheme,
    string Host,
    string Path,
    string? ContentType,
    long RequestBytes);

/// <summary>
/// The telemetry of one request, from the moment it was seen to the moment its response was
/// delivered. The only thing in the forwarding path that publishes request and exchange
/// events; the transformer and the mutation report facts to it and nothing else.
///
/// It exists because of one awkwardness in the protocol: <c>request.observed</c> carries the
/// treatment -- passthrough, clean or treated -- which is only known after the body has been
/// scanned. So the announcement is held back until the decision is made and released just
/// ahead of the first exchange event. The pump delivers in publish order, so a burst is fine;
/// each event still carries the time its step really happened.
///
/// The guarantees, whatever path the request took: <c>request.observed</c> is published exactly
/// once and before anything else about the request; a treated exchange always reaches
/// <c>exchange.delivered</c>, with the steps it never got to filled in as empty, so a packet on
/// the dashboard's band never stalls; <c>request.completed</c> is last. Nothing here throws.
///
/// One instance per request, touched from that request's own flow only. The observer half is
/// what a body mutation sees of it.
/// </summary>
sealed class ExchangeTrace : IExchangeObserver
{
    /// <summary>Longest body text put into an event. A large upload is a display problem, not
    /// a telemetry one; offsets past the cut are still correct against the uncut body, and the
    /// cut is marked.</summary>
    internal const int MaxBodyChars = 64 * 1024;

    private const string Cut = "…";

    private readonly ITelemetrySink _sink;
    private readonly string _requestId;
    private readonly RequestFacts _facts;
    private readonly PrivacyAssessor? _privacy;
    private readonly long _openedAtTimestamp = Stopwatch.GetTimestamp();

    // The announcement is published late but describes the moment the request was seen.
    private readonly long _seenAt = TelemetryJson.Now();

    private bool _observed;
    private bool _exchange;
    private bool _completed;
    private string? _exchangeId;

    // Facts reported along the way, released by the decisions below.
    //
    // Bodies are kept as the bytes they arrived as and decoded only when an event needs the
    // text -- which, for the common clean request, is never. A mutation that decoded them
    // anyway hands the text over (RequestText, RewrittenText, ResponseText), so the same bytes
    // are not decoded twice on the rare treated path either.
    private ReadOnlyMemory<byte> _requestBytes;
    private BodyDescriptor? _requestDescriptor;
    private string? _requestText;
    private string? _rewrittenText;
    private long _bodyBufferedAt;
    private long _detectionStartedAt;
    private IReadOnlyList<DetectedEntity> _entities = [];
    private DetectionStats _stats = DetectionStats.None;
    private long _detectedAt;
    private long _detectedAtTimestamp;

    private long _dispatchedAtTimestamp;
    private bool _dispatched;

    private int? _responseStatus;
    private string? _tokenizedResponseBody;
    private ReadOnlyMemory<byte> _responseBytes;
    private BodyDescriptor? _responseDescriptor;
    private long _respondedAt;
    private long _respondedAtTimestamp;
    private double _upstreamMs;
    private bool _responded;

    private long _responseBufferedAtTimestamp;

    private string? _restoredBody;
    private int _restoredCount;
    private long _restoredAt;
    private long _restoredAtTimestamp;
    private bool _restored;

    public ExchangeTrace(ITelemetrySink sink, string requestId, RequestFacts facts, PrivacyAssessor? privacy = null)
    {
        _sink = sink;
        _requestId = requestId;
        _facts = facts;
        _privacy = privacy;
    }

    public string RequestId => _requestId;

    /// <summary>Set once the body was buffered; null for a request that never got that far.</summary>
    public string? ExchangeId => _exchangeId;

    /// <summary>The request is forwarded as it arrived, for <paramref name="reason"/>.</summary>
    public void Passthrough(string reason)
    {
        if (_observed)
            return;

        Observe(Treatment.Passthrough, reason, exchangeId: null);
    }

    /// <summary>The request body is in hand and about to be offered to the mutation.</summary>
    public void BodyBuffered(ReadOnlyMemory<byte> body, BodyDescriptor descriptor)
    {
        if (_observed || _exchangeId is not null)
            return;

        _exchangeId = CorrelationIds.NextExchange();
        _requestBytes = body;
        _requestDescriptor = descriptor;
        _bodyBufferedAt = TelemetryJson.Now();
        _detectionStartedAt = Stopwatch.GetTimestamp();
    }

    public void RequestText(string text) => _requestText ??= text;

    public void RewrittenText(string text) => _rewrittenText ??= text;

    public void ResponseText(string text)
    {
        if (string.IsNullOrEmpty(_tokenizedResponseBody))
            _tokenizedResponseBody = text;
    }

    public void Detected(IReadOnlyList<DetectedEntity> entities, DetectionStats stats)
    {
        if (_observed)
            return;

        _entities = entities;
        _stats = stats;
        _detectedAt = TelemetryJson.Now();
        _detectedAtTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// The mutation returned. This is where the treatment is decided: something was found and
    /// replaced, or the body was read and found clean.
    /// </summary>
    public void RequestRewritten(byte[]? mutated, BodyDescriptor descriptor)
    {
        if (_observed)
            return;

        if (_entities.Count == 0 || mutated is null)
        {
            Observe(Treatment.Clean, CleanReason(), exchangeId: null);

            return;
        }

        string id = _exchangeId ??= CorrelationIds.NextExchange();
        long now = TelemetryJson.Now();
        long openedAt = _bodyBufferedAt == 0 ? now : _bodyBufferedAt;

        Observe(Treatment.Treated, IdentifiersReason(_entities.Count), id);

        _exchange = true;

        _sink.Publish(new ExchangeOpened(
            id,
            _requestId,
            openedAt,
            _facts.ClientLabel,
            _facts.Method,
            _facts.Scheme,
            _facts.Host,
            _facts.Path,
            _facts.ContentType ?? string.Empty,
            Capped(RequestBody)));

        _sink.Publish(new DetectionCompleted(
            id,
            _detectedAt == 0 ? now : _detectedAt,
            _entities,
            _stats.ScannedMs > 0 ? _stats.ScannedMs : Stopwatch.GetElapsedTime(_detectionStartedAt).TotalMilliseconds,
            _entities.Average(entity => entity.Confidence),
            _entities.GroupBy(entity => entity.Kind).ToDictionary(group => group.Key, group => group.Count()),
            _stats.Suppressed,
            _stats.NearMisses));

        string redacted = _rewrittenText ?? descriptor.Encoding.GetString(mutated);

        _sink.Publish(new RedactionCompleted(id, now, Capped(redacted)));

        // Off the request path: the check takes seconds and the answer is for the dashboard,
        // not for this response. The full text goes, not the capped one.
        _privacy?.Schedule(id, redacted, _entities);
    }

    /// <summary>The mutation failed and the request is not being forwarded.</summary>
    public void RequestRefused(Exception exception)
    {
        if (_observed)
            return;

        Observe(Treatment.Passthrough, "not forwarded: rewrite failed", exchangeId: null);

        _sink.Publish(new ProxyLog(
            TelemetryJson.Now(),
            TelemetryLogLevel.Block,
            $"Request to {_facts.Host} was not forwarded: the body could not be rewritten ({exception.Message}).",
            _exchangeId));
    }

    /// <summary>The request has left for <paramref name="target"/>.</summary>
    public void Dispatched(string target, long bytes)
    {
        if (_dispatched)
            return;

        _dispatched = true;
        _dispatchedAtTimestamp = Stopwatch.GetTimestamp();

        if (!_exchange || _exchangeId is null)
            return;

        _sink.Publish(new UpstreamDispatched(_exchangeId, TelemetryJson.Now(), target, bytes));
    }

    /// <summary>
    /// The destination answered. May be called more than once: first with the status alone as
    /// soon as the headers are in, then with the body once it has been read. The event goes out
    /// at the last moment -- when the response is restored, or the request completes -- so the
    /// later, fuller call wins.
    /// </summary>
    public void Responded(int status, string tokenizedBody)
    {
        if (_responded)
            return;

        if (_responseStatus is null)
        {
            _respondedAt = TelemetryJson.Now();
            _respondedAtTimestamp = Stopwatch.GetTimestamp();
            _upstreamMs = _dispatched
                ? Stopwatch.GetElapsedTime(_dispatchedAtTimestamp).TotalMilliseconds
                : Stopwatch.GetElapsedTime(_openedAtTimestamp).TotalMilliseconds;
        }

        _responseStatus = status;

        if (tokenizedBody.Length > 0)
            _tokenizedResponseBody = tokenizedBody;
    }

    public void ResponseBuffered()
    {
        if (_responseBufferedAtTimestamp == 0)
            _responseBufferedAtTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>The same, with the decoded body in hand -- kept as bytes until an event
    /// needs the text, or until the mutation hands the text over itself.</summary>
    public void ResponseBuffered(ReadOnlyMemory<byte> body, BodyDescriptor descriptor)
    {
        ResponseBuffered();

        _responseBytes = body;
        _responseDescriptor = descriptor;
    }

    public void Restored(string responseBody, int restored)
    {
        if (_restored)
            return;

        _restoredBody = responseBody;
        _restoredCount = restored;
        _restoredAt = TelemetryJson.Now();
        _restoredAtTimestamp = Stopwatch.GetTimestamp();

        FlushResponded();
        FlushRestored();
    }

    /// <summary>The response has been delivered, or the attempt has ended. Always last.</summary>
    public void Completed(int status, long responseBytes, double durationMs)
    {
        if (_completed)
            return;

        _completed = true;

        if (!_observed)
            Observe(Treatment.Passthrough, "not inspected", exchangeId: null);

        if (_exchange && _exchangeId is not null)
        {
            if (!_dispatched)
                Dispatched(_facts.Host, _facts.RequestBytes);

            Responded(status, string.Empty);
            FlushResponded();
            FlushRestored();

            double totalMs = Stopwatch.GetElapsedTime(_openedAtTimestamp).TotalMilliseconds;

            _sink.Publish(new ExchangeDelivered(_exchangeId, TelemetryJson.Now(), totalMs, Timing(totalMs)));
        }

        _sink.Publish(new RequestCompleted(_requestId, TelemetryJson.Now(), status, responseBytes, durationMs));
    }

    private void Observe(Treatment treatment, string reason, string? exchangeId)
    {
        _observed = true;

        _sink.Publish(new RequestObserved(
            _requestId,
            _seenAt,
            _facts.ClientIp,
            _facts.ClientLabel,
            _facts.Method,
            _facts.Scheme,
            _facts.Host,
            _facts.Path,
            _facts.ContentType,
            _facts.RequestBytes,
            treatment,
            reason,
            exchangeId));
    }

    private void FlushResponded()
    {
        if (_responded || !_exchange || _exchangeId is null || _responseStatus is not int status)
            return;

        _responded = true;

        _sink.Publish(new UpstreamResponded(
            _exchangeId,
            _respondedAt,
            status,
            Capped(TokenizedResponseBody),
            _upstreamMs));
    }

    private void FlushRestored()
    {
        if (_restored || !_exchange || _exchangeId is null || !_responded)
            return;

        _restored = true;

        _sink.Publish(new RehydrationCompleted(
            _exchangeId,
            _restoredAt == 0 ? TelemetryJson.Now() : _restoredAt,
            Capped(_restoredBody ?? TokenizedResponseBody),
            _restoredCount));
    }

    /// <summary>The request body as text: what the mutation reported, or the buffered bytes
    /// decoded once, here, the first time an event asks.</summary>
    private string RequestBody
        => _requestText ??= _requestDescriptor is null
            ? string.Empty
            : _requestDescriptor.Encoding.GetString(_requestBytes.Span);

    /// <summary>The response body before anything was put back, on the same terms.</summary>
    private string TokenizedResponseBody
        => _tokenizedResponseBody ??= _responseDescriptor is null
            ? string.Empty
            : _responseDescriptor.Encoding.GetString(_responseBytes.Span);

    /// <summary>
    /// The round trip split by step, from the Stopwatch instants above. A step the exchange
    /// never reached is 0, and the rest of the total -- response body on the wire, framing,
    /// delivery to the client -- is the overhead.
    /// </summary>
    private ExchangeTiming Timing(double totalMs)
    {
        double bufferMs = Between(_openedAtTimestamp, _detectionStartedAt);
        double detectMs = Between(_detectionStartedAt, _detectedAtTimestamp);
        double rehydrateMs = Between(
            _responseBufferedAtTimestamp != 0 ? _responseBufferedAtTimestamp : _respondedAtTimestamp,
            _restoredAtTimestamp);
        double overheadMs = Math.Max(0, totalMs - bufferMs - detectMs - _upstreamMs - rehydrateMs);

        return new ExchangeTiming(bufferMs, detectMs, _upstreamMs, rehydrateMs, overheadMs);
    }

    private static double Between(long from, long to)
        => from == 0 || to == 0 || to < from ? 0 : Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

    private string CleanReason()
    {
        string size = Size(_requestDescriptor is null ? _facts.RequestBytes : _requestBytes.Length);
        string misses = _stats.NearMisses.Count switch
        {
            0 => string.Empty,
            1 => " (1 near miss)",
            int count => $" ({count} near misses)",
        };

        return _facts.ContentType is { Length: > 0 } contentType
            ? $"nothing found in {size} of {MediaType(contentType)}{misses}"
            : $"nothing found in {size}{misses}";
    }

    private static string IdentifiersReason(int count)
        => count == 1 ? "1 identifier" : $"{count} identifiers";

    private static string MediaType(string contentType)
    {
        int semicolon = contentType.IndexOf(';');

        return (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
    }

    private static string Size(long bytes)
        => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        };

    internal static string Capped(string text)
        => text.Length <= MaxBodyChars ? text : string.Concat(text.AsSpan(0, MaxBodyChars), Cut);
}
