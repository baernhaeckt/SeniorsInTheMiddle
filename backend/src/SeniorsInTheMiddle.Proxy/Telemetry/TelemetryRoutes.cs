namespace SeniorsInTheMiddle.Proxy.Telemetry;

public static class TelemetryRoutes
{
    /// <summary>
    /// Where the dashboard attaches. Origin form, so <c>ForwardProxyMiddleware</c> lets it
    /// through to the endpoint instead of treating it as something to forward.
    /// </summary>
    public const string HubPath = "/hub/telemetry";
}
