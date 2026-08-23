namespace SeniorsInTheMiddle.Proxy.Telemetry;

/// <summary>The SignalR path the dashboard subscribes to, shared by the hub and the middleware
/// that has to recognise it as local rather than something to forward.</summary>
public static class TelemetryRoutes
{
    /// <summary>
    /// Where the dashboard attaches. Origin form, so <c>ForwardProxyMiddleware</c> lets it
    /// through to the endpoint instead of treating it as something to forward.
    /// </summary>
    public const string HubPath = "/hub/telemetry";
}
