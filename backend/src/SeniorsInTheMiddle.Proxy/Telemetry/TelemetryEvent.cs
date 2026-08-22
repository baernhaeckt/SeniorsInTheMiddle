using System.Text.Json.Serialization;

namespace SeniorsInTheMiddle.Proxy.Telemetry;

/// <summary>
/// The wire contract with the dashboard. Every record here mirrors a valibot schema in
/// frontend/src/protocol/types.ts, field for field; a frame that does not match is dropped
/// by the browser and counted in the header's badge rather than shown.
///
/// The discriminator is written as "type" so it lines up with the frontend's
/// v.variant('type', ...). Serialize through <see cref="TelemetryJson"/>, never by runtime
/// type, or the discriminator is silently left out.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ServerHello), "hello")]
[JsonDerivedType(typeof(RequestObserved), "request.observed")]
[JsonDerivedType(typeof(RequestCompleted), "request.completed")]
[JsonDerivedType(typeof(ExchangeOpened), "exchange.opened")]
[JsonDerivedType(typeof(DetectionCompleted), "detection.completed")]
[JsonDerivedType(typeof(RedactionCompleted), "redaction.completed")]
[JsonDerivedType(typeof(UpstreamDispatched), "upstream.dispatched")]
[JsonDerivedType(typeof(UpstreamResponded), "upstream.responded")]
[JsonDerivedType(typeof(RehydrationCompleted), "rehydration.completed")]
[JsonDerivedType(typeof(ExchangeDelivered), "exchange.delivered")]
[JsonDerivedType(typeof(PrivacyAssessed), "privacy.assessed")]
[JsonDerivedType(typeof(ProxyLog), "log")]
public abstract record TelemetryEvent;

/// <summary>What the dashboard reads on connect. The version has to match PROTOCOL_VERSION.</summary>
public sealed record ServerHello(int Version, ProxyInfo Proxy, ProxyPolicy Policy) : TelemetryEvent;

public sealed record ProxyInfo(string Name, string Region, string Mode, string Policy);

/// <summary>
/// What this deployment does and does not look at, so the dashboard can say so instead of
/// leaving the operator to infer it from the traffic.
/// </summary>
/// <param name="Rewrite">True when bodies are rewritten; false when the proxy only watches.</param>
/// <param name="BypassHosts">Hosts tunnelled unread -- see Forwarding.InterceptionBypass.</param>
/// <param name="InspectOnly">Hosts on which only the listed paths are inspected.</param>
/// <param name="MaxBodyBytes">Largest body offered to the mutation.</param>
/// <param name="ConfidenceThreshold">The detector's score below which a finding is a near miss.</param>
public sealed record ProxyPolicy(
    bool Rewrite,
    IReadOnlyList<string> BypassHosts,
    IReadOnlyDictionary<string, string[]> InspectOnly,
    long MaxBodyBytes,
    double ConfidenceThreshold,
    ServiceStates Services);

public sealed record ServiceStates(ServiceState Pii, ServiceState PrivacyCheck);

[JsonConverter(typeof(JsonStringEnumConverter<ServiceState>))]
public enum ServiceState
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("disabled")] Disabled,
    [JsonStringEnumMemberName("down")] Down,
}

/// <summary>
/// A request the proxy saw. Always followed by a <see cref="RequestCompleted"/> with the
/// same requestId; the dashboard's traffic row is created here and patched there.
/// </summary>
public sealed record RequestObserved(
    string RequestId,
    long At,
    string ClientIp,
    string ClientLabel,
    string Method,
    TelemetryScheme Scheme,
    string Host,
    string Path,
    string? ContentType,
    long RequestBytes,
    Treatment Treatment,
    string Reason,
    string? ExchangeId = null) : TelemetryEvent;

public sealed record RequestCompleted(
    string RequestId,
    long At,
    int Status,
    long ResponseBytes,
    double DurationMs) : TelemetryEvent;

public sealed record ExchangeOpened(
    string ExchangeId,
    string RequestId,
    long At,
    string ClientLabel,
    string Method,
    TelemetryScheme Scheme,
    string Host,
    string Path,
    string ContentType,
    string RequestBody) : TelemetryEvent;

/// <param name="RiskScoreMean">Mean confidence over the entities; absent when there are none.</param>
/// <param name="TypeFrequencies">How many of each kind were replaced.</param>
/// <param name="Suppressed">Findings the detector reported that were not replaced on their own:
/// nested in another, or not placeable on the text.</param>
/// <param name="NearMisses">Findings below the confidence threshold. Not replaced.</param>
public sealed record DetectionCompleted(
    string ExchangeId,
    long At,
    IReadOnlyList<DetectedEntity> Entities,
    double ScannedMs,
    double? RiskScoreMean,
    IReadOnlyDictionary<string, int> TypeFrequencies,
    int Suppressed,
    IReadOnlyList<NearMiss> NearMisses) : TelemetryEvent;

/// <summary>
/// One identifier found in a request body. <c>Value</c> is the real text — it reaches the
/// dashboard and nothing else.
/// </summary>
/// <param name="InformationType">The detector's human-readable label for the kind.</param>
/// <param name="RiskLevel">1..3 after Schwartz &amp; Solove: not, semi, fully identifiable.</param>
/// <param name="HipaaCategory">Whether the kind is protected health information.</param>
public sealed record DetectedEntity(
    string Id,
    string Kind,
    string Value,
    string Token,
    int Start,
    int End,
    double Confidence,
    string InformationType = "",
    int RiskLevel = 0,
    string HipaaCategory = "");

/// <summary>A finding that scored below the threshold. No offsets: in a JSON body they would
/// index the analysed values, not the document, and a wrong offset is worse than none.</summary>
public sealed record NearMiss(string Kind, string Value, double Confidence);

public sealed record RedactionCompleted(
    string ExchangeId,
    long At,
    string RedactedRequestBody) : TelemetryEvent;

public sealed record UpstreamDispatched(
    string ExchangeId,
    long At,
    string Target,
    long Bytes) : TelemetryEvent;

public sealed record UpstreamResponded(
    string ExchangeId,
    long At,
    int Status,
    string TokenizedResponseBody,
    double UpstreamMs) : TelemetryEvent;

public sealed record RehydrationCompleted(
    string ExchangeId,
    long At,
    string ResponseBody,
    int Restored) : TelemetryEvent;

public sealed record ExchangeDelivered(
    string ExchangeId,
    long At,
    double TotalMs,
    ExchangeTiming Timing) : TelemetryEvent;

/// <summary>Where the round trip went. Steps the exchange never reached are 0.</summary>
/// <param name="BufferMs">Reading the request body off the client.</param>
/// <param name="DetectMs">The mutation, scan and rewrite together.</param>
/// <param name="UpstreamMs">Dispatch until the destination's headers.</param>
/// <param name="RehydrateMs">Restoring the response body.</param>
/// <param name="OverheadMs">Everything else: response body on the wire, framing, delivery.</param>
public sealed record ExchangeTiming(
    double BufferMs,
    double DetectMs,
    double UpstreamMs,
    double RehydrateMs,
    double OverheadMs)
{
    public static readonly ExchangeTiming None = new(0, 0, 0, 0, 0);
}

/// <summary>
/// How likely it is that a replaced name can still be recovered from the redacted text. Runs
/// off the request path and arrives late -- usually after <see cref="ExchangeDelivered"/>.
/// </summary>
public sealed record PrivacyAssessed(
    string ExchangeId,
    long At,
    IReadOnlyList<PrivacyRiskEntry> Risks,
    double MaxProbability,
    double AssessedMs,
    PrivacyStatus Status,
    string? Reason = null) : TelemetryEvent;

public sealed record PrivacyRiskEntry(string Token, double Probability);

[JsonConverter(typeof(JsonStringEnumConverter<PrivacyStatus>))]
public enum PrivacyStatus
{
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("skipped")] Skipped,
    [JsonStringEnumMemberName("failed")] Failed,
}

/// <summary>A line for the dashboard's ticker. Not tied to a request unless it says so.</summary>
public sealed record ProxyLog(
    long At,
    TelemetryLogLevel Level,
    string Message,
    string? ExchangeId = null) : TelemetryEvent;

[JsonConverter(typeof(JsonStringEnumConverter<TelemetryScheme>))]
public enum TelemetryScheme
{
    [JsonStringEnumMemberName("http")] Http,
    [JsonStringEnumMemberName("https")] Https,
}

/// <summary>
/// What the proxy did with a request body. Nothing is <c>Treated</c> until a detector
/// exists; until then a body that was read and found unremarkable is <c>Clean</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Treatment>))]
public enum Treatment
{
    /// <summary>Non-sensitive by type: stylesheets, scripts, fonts, images. Body never read.</summary>
    [JsonStringEnumMemberName("passthrough")] Passthrough,

    /// <summary>Body was read, nothing identifying in it.</summary>
    [JsonStringEnumMemberName("clean")] Clean,

    /// <summary>Identifiers found and replaced before the request left.</summary>
    [JsonStringEnumMemberName("treated")] Treated,
}

[JsonConverter(typeof(JsonStringEnumConverter<TelemetryLogLevel>))]
public enum TelemetryLogLevel
{
    [JsonStringEnumMemberName("info")] Info,
    [JsonStringEnumMemberName("warn")] Warn,
    [JsonStringEnumMemberName("block")] Block,
}

/// <summary>
/// The kinds the dashboard's demo feed uses. Not on the wire: <see cref="DetectedEntity.Kind"/>
/// is the detector's own category name, sent verbatim, and the dashboard accepts any.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EntityKind>))]
public enum EntityKind
{
    [JsonStringEnumMemberName("PERSON")] Person,
    [JsonStringEnumMemberName("AHV")] Ahv,
    [JsonStringEnumMemberName("IBAN")] Iban,
    [JsonStringEnumMemberName("ADDRESS")] Address,
    [JsonStringEnumMemberName("PHONE")] Phone,
    [JsonStringEnumMemberName("EMAIL")] Email,
    [JsonStringEnumMemberName("BIRTHDATE")] Birthdate,
    [JsonStringEnumMemberName("HEALTH")] Health,
    [JsonStringEnumMemberName("INSURANCE")] Insurance,
}
