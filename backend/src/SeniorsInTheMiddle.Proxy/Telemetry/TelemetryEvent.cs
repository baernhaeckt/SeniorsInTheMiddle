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
[JsonDerivedType(typeof(ProxyLog), "log")]
public abstract record TelemetryEvent;

/// <summary>What the dashboard reads on connect. The version has to match PROTOCOL_VERSION.</summary>
public sealed record ServerHello(int Version, ProxyInfo Proxy) : TelemetryEvent;

public sealed record ProxyInfo(string Name, string Region, string Mode, string Policy);

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

public sealed record DetectionCompleted(
    string ExchangeId,
    long At,
    IReadOnlyList<DetectedEntity> Entities,
    double ScannedMs) : TelemetryEvent;

/// <summary>
/// One identifier found in a request body. <c>Value</c> is the real text — it reaches the
/// dashboard and nothing else.
/// </summary>
public sealed record DetectedEntity(
    string Id,
    EntityKind Kind,
    string Value,
    string Token,
    int Start,
    int End,
    double Confidence);

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
    double TotalMs) : TelemetryEvent;

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
