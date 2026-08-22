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
/// treatment -- passthrough, clean or treated -- and the treatment is only known after the
/// body has been scanned. So the announcement is held back until the decision is made, and
/// released just ahead of the first exchange event. The pump delivers in publish order, so a
/// burst is fine; each event still carries the time its step really happened.
///
/// The guarantees, whatever path the request took: <c>request.observed</c> is published exactly
/// once and before anything else about the request; a treated exchange always reaches
/// <c>exchange.delivered</c>, with the steps it never got to filled in as empty, so a packet
/// on the dashboard's band never stalls; <c>request.completed</c> is last. Nothing here throws.
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

    private readonly ITelemetrySink sink;
    private readonly string requestId;
    private readonly RequestFacts facts;
    private readonly long openedAtTimestamp = Stopwatch.GetTimestamp();

    // The announcement is published late but describes the moment the request was seen.
    private readonly long seenAt = TelemetryJson.Now();

    private bool observed;
    private bool exchange;
    private bool completed;
    private string? exchangeId;

    // Facts reported along the way, released by the decisions below.
    private string? requestBody;
    private long bodyBufferedAt;
    private long detectionStartedAt;
    private IReadOnlyList<DetectedEntity> entities = [];
    private double scannedMs;
    private long detectedAt;

    private long dispatchedAtTimestamp;
    private bool dispatched;

    private int? responseStatus;
    private string tokenizedResponseBody = string.Empty;
    private long respondedAt;
    private double upstreamMs;
    private bool responded;

    private string? restoredBody;
    private int restoredCount;
    private long restoredAt;
    private bool restored;

    public ExchangeTrace(ITelemetrySink sink, string requestId, RequestFacts facts)
    {
        this.sink = sink;
        this.requestId = requestId;
        this.facts = facts;
    }

    public string RequestId => requestId;

    /// <summary>Set once the body was buffered; null for a request that never got that far.</summary>
    public string? ExchangeId => exchangeId;

    /// <summary>The request is forwarded as it arrived, for <paramref name="reason"/>.</summary>
    public void Passthrough(string reason)
    {
        if (observed)
            return;

        Observe(Treatment.Passthrough, reason, exchangeId: null);
    }

    /// <summary>The request body is in hand and about to be offered to the mutation.</summary>
    public void BodyBuffered(ReadOnlyMemory<byte> body, BodyDescriptor descriptor)
    {
        if (observed || exchangeId is not null)
            return;

        exchangeId = CorrelationIds.NextExchange();
        requestBody = descriptor.Encoding.GetString(body.Span);
        bodyBufferedAt = TelemetryJson.Now();
        detectionStartedAt = Stopwatch.GetTimestamp();
    }

    public void Detected(IReadOnlyList<DetectedEntity> entities, double scannedMs)
    {
        if (observed)
            return;

        this.entities = entities;
        this.scannedMs = scannedMs;
        detectedAt = TelemetryJson.Now();
    }

    /// <summary>
    /// The mutation returned. This is where the treatment is decided: something was found and
    /// replaced, or the body was read and found clean.
    /// </summary>
    public void RequestRewritten(byte[]? mutated, BodyDescriptor descriptor)
    {
        if (observed)
            return;

        if (entities.Count == 0 || mutated is null)
        {
            Observe(Treatment.Clean, CleanReason(), exchangeId: null);

            return;
        }

        string id = exchangeId ??= CorrelationIds.NextExchange();
        long now = TelemetryJson.Now();
        long openedAt = bodyBufferedAt == 0 ? now : bodyBufferedAt;

        Observe(Treatment.Treated, IdentifiersReason(entities.Count), id);

        exchange = true;

        sink.Publish(new ExchangeOpened(
            id,
            requestId,
            openedAt,
            facts.ClientLabel,
            facts.Method,
            facts.Scheme,
            facts.Host,
            facts.Path,
            facts.ContentType ?? string.Empty,
            Capped(requestBody ?? string.Empty)));

        sink.Publish(new DetectionCompleted(
            id,
            detectedAt == 0 ? now : detectedAt,
            entities,
            scannedMs > 0 ? scannedMs : Stopwatch.GetElapsedTime(detectionStartedAt).TotalMilliseconds));

        sink.Publish(new RedactionCompleted(
            id,
            now,
            Capped(descriptor.Encoding.GetString(mutated))));
    }

    /// <summary>The mutation failed and the request is not being forwarded.</summary>
    public void RequestRefused(Exception exception)
    {
        if (observed)
            return;

        Observe(Treatment.Passthrough, "not forwarded: rewrite failed", exchangeId: null);

        sink.Publish(new ProxyLog(
            TelemetryJson.Now(),
            TelemetryLogLevel.Block,
            $"Request to {facts.Host} was not forwarded: the body could not be rewritten ({exception.Message}).",
            exchangeId));
    }

    /// <summary>The request has left for <paramref name="target"/>.</summary>
    public void Dispatched(string target, long bytes)
    {
        if (dispatched)
            return;

        dispatched = true;
        dispatchedAtTimestamp = Stopwatch.GetTimestamp();

        if (!exchange || exchangeId is null)
            return;

        sink.Publish(new UpstreamDispatched(exchangeId, TelemetryJson.Now(), target, bytes));
    }

    /// <summary>
    /// The destination answered. May be called more than once: first with the status alone as
    /// soon as the headers are in, then with the body once it has been read. The event goes out
    /// at the last moment -- when the response is restored, or the request completes -- so the
    /// later, fuller call wins.
    /// </summary>
    public void Responded(int status, string tokenizedBody)
    {
        if (responded)
            return;

        if (responseStatus is null)
        {
            respondedAt = TelemetryJson.Now();
            upstreamMs = dispatched
                ? Stopwatch.GetElapsedTime(dispatchedAtTimestamp).TotalMilliseconds
                : Stopwatch.GetElapsedTime(openedAtTimestamp).TotalMilliseconds;
        }

        responseStatus = status;

        if (tokenizedBody.Length > 0)
            tokenizedResponseBody = tokenizedBody;
    }

    public void Restored(string responseBody, int restored)
    {
        if (this.restored)
            return;

        restoredBody = responseBody;
        restoredCount = restored;
        restoredAt = TelemetryJson.Now();

        FlushResponded();
        FlushRestored();
    }

    /// <summary>The response has been delivered, or the attempt has ended. Always last.</summary>
    public void Completed(int status, long responseBytes, double durationMs)
    {
        if (completed)
            return;

        completed = true;

        if (!observed)
            Observe(Treatment.Passthrough, "not inspected", exchangeId: null);

        if (exchange && exchangeId is not null)
        {
            if (!dispatched)
                Dispatched(facts.Host, facts.RequestBytes);

            Responded(status, string.Empty);
            FlushResponded();
            FlushRestored();

            sink.Publish(new ExchangeDelivered(
                exchangeId,
                TelemetryJson.Now(),
                Stopwatch.GetElapsedTime(openedAtTimestamp).TotalMilliseconds));
        }

        sink.Publish(new RequestCompleted(requestId, TelemetryJson.Now(), status, responseBytes, durationMs));
    }

    private void Observe(Treatment treatment, string reason, string? exchangeId)
    {
        observed = true;

        sink.Publish(new RequestObserved(
            requestId,
            seenAt,
            facts.ClientIp,
            facts.ClientLabel,
            facts.Method,
            facts.Scheme,
            facts.Host,
            facts.Path,
            facts.ContentType,
            facts.RequestBytes,
            treatment,
            reason,
            exchangeId));
    }

    private void FlushResponded()
    {
        if (responded || !exchange || exchangeId is null || responseStatus is not int status)
            return;

        responded = true;

        sink.Publish(new UpstreamResponded(
            exchangeId,
            respondedAt,
            status,
            Capped(tokenizedResponseBody),
            upstreamMs));
    }

    private void FlushRestored()
    {
        if (restored || !exchange || exchangeId is null || !responded)
            return;

        restored = true;

        sink.Publish(new RehydrationCompleted(
            exchangeId,
            restoredAt == 0 ? TelemetryJson.Now() : restoredAt,
            Capped(restoredBody ?? tokenizedResponseBody),
            restoredCount));
    }

    private string CleanReason()
    {
        string size = Size(requestBody is null ? facts.RequestBytes : Encoding.UTF8.GetByteCount(requestBody));

        return facts.ContentType is { Length: > 0 } contentType
            ? $"nothing found in {size} of {MediaType(contentType)}"
            : $"nothing found in {size}";
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
