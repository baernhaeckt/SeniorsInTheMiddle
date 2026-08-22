namespace SeniorsInTheMiddle.Proxy.Telemetry;

/// <summary>
/// Where the proxy hands events off. Every call is on a request or tunnel thread, so this
/// never blocks, never throws and never waits for a dashboard: an event that cannot be
/// queued is dropped and counted.
/// </summary>
public interface ITelemetrySink
{
    void Publish(TelemetryEvent telemetryEvent);
}

/// <summary>Convenience for the one-liners, which are most of the call sites.</summary>
public static class TelemetrySinkExtensions
{
    public static void Info(this ITelemetrySink sink, string message)
        => sink.Publish(new ProxyLog(TelemetryJson.Now(), TelemetryLogLevel.Info, message));

    public static void Warn(this ITelemetrySink sink, string message)
        => sink.Publish(new ProxyLog(TelemetryJson.Now(), TelemetryLogLevel.Warn, message));

    public static void Block(this ITelemetrySink sink, string message)
        => sink.Publish(new ProxyLog(TelemetryJson.Now(), TelemetryLogLevel.Block, message));
}
