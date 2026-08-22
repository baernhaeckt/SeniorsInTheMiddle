using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeniorsInTheMiddle.Proxy.Telemetry;

/// <summary>
/// One place that turns a <see cref="TelemetryEvent"/> into the exact text the dashboard
/// expects, so the hub and the tests cannot disagree about it.
/// </summary>
public static class TelemetryJson
{
    /// <summary>
    /// Must match PROTOCOL_VERSION in frontend/src/protocol/types.ts, or the dashboard
    /// header shows a mismatch banner.
    /// </summary>
    public const int ProtocolVersion = 3;

    /// <summary>
    /// Nulls are omitted rather than written, because the optional fields in the protocol
    /// are valibot's v.optional, which accepts a missing key but not an explicit null.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // The traffic this watches is German and Swiss, so the default encoder would spend
        // six bytes on every umlaut. These frames are JSON on a socket and are rendered as
        // text nodes, never as markup, so the relaxed encoder is the right one here.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serialize through the base type. Serializing an already-narrowed record would leave
    /// out the polymorphic "type" discriminator and the frame would be rejected.
    /// </summary>
    public static string Serialize(TelemetryEvent telemetryEvent)
        => JsonSerializer.Serialize(telemetryEvent, Options);

    /// <summary>Epoch milliseconds, the protocol's only clock.</summary>
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
