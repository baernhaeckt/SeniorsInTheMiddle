namespace SeniorsInTheMiddle.Proxy.Telemetry;

/// <summary>
/// How this proxy introduces itself. Shown in the dashboard header, so it should say which
/// deployment you are looking at.
/// </summary>
sealed class TelemetryDescriptor
{
    public TelemetryDescriptor(IConfiguration configuration)
    {
        Hello = new ServerHello(
            TelemetryJson.ProtocolVersion,
            new ProxyInfo(
                Name: configuration.GetValue("Proxy:Name", "Seniors in the Middle")!,
                Region: configuration.GetValue("Proxy:Region", Environment.MachineName)!,
                Mode: "intercept",
                Policy: configuration.GetValue("Proxy:Policy", "observe-only")!));
    }

    public ServerHello Hello { get; }
}
