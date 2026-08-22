using SeniorsInTheMiddle.Proxy.Forwarding;
using SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;
using SeniorsInTheMiddle.Proxy.Services;

namespace SeniorsInTheMiddle.Proxy.Telemetry;

/// <summary>
/// How this proxy introduces itself. Shown in the dashboard header, so it should say which
/// deployment you are looking at -- and what it does: whether bodies are rewritten, what is
/// left unread, where the confidence line is, and whether the detectors are answering.
///
/// The policy is read from the running configuration rather than from a setting that names
/// it, so the hello cannot claim "observe-only" while the proxy rewrites. The service states
/// are the one part that changes, so they are pinged per connection.
/// </summary>
sealed class TelemetryDescriptor
{
    /// <summary>Short: a dashboard connecting should not wait on a detector that is down.</summary>
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Same variable the python side reads -- see backend/Dockerfile.</summary>
    public const string ThresholdVariable = "PII_SCORE_THRESHOLD";

    public const double DefaultThreshold = 0.6;

    private readonly ServiceConnections services;
    private readonly ProxyInfo proxy;
    private readonly ProxyPolicy policy;

    public TelemetryDescriptor(
        IConfiguration configuration,
        BodyLimits limits,
        InterceptionBypass bypass,
        InspectionScope scope,
        IBodyMutationFactory mutations,
        ServiceConnections services)
    {
        this.services = services;

        bool rewrite = mutations is ReplacerService && limits.MaxMutableBodyBytes > 0;

        proxy = new ProxyInfo(
            Name: configuration.GetValue("Proxy:Name", "Seniors in the Middle")!,
            Region: configuration.GetValue("Proxy:Region", Environment.MachineName)!,
            Mode: "intercept",
            Policy: rewrite ? "rewrite" : "observe-only");

        policy = new ProxyPolicy(
            rewrite,
            bypass.Hosts,
            scope.Scoped,
            limits.MaxMutableBodyBytes,
            configuration.GetValue(ThresholdVariable, DefaultThreshold),
            new ServiceStates(ServiceState.Disabled, ServiceState.Disabled));
    }

    /// <summary>The hello without asking the services, for tests and for the static parts.</summary>
    public ServerHello Hello => new(TelemetryJson.ProtocolVersion, proxy, policy);

    /// <summary>The hello with each service pinged. Never throws: a service that does not
    /// answer in time is <see cref="ServiceState.Down"/>.</summary>
    public async Task<ServerHello> HelloAsync(CancellationToken cancellationToken)
    {
        ServiceState pii = await StateAsync(ServiceConnections.PiiService, cancellationToken);
        ServiceState privacy = await StateAsync(ServiceConnections.PrivacyCheckService, cancellationToken);

        return Hello with { Policy = policy with { Services = new ServiceStates(pii, privacy) } };
    }

    private async Task<ServiceState> StateAsync(string name, CancellationToken cancellationToken)
    {
        ServiceConnection service = services.Get(name);

        if (!service.IsConfigured)
            return ServiceState.Disabled;

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PingTimeout);

        try
        {
            await service.PingAsync(timeout.Token);
            return ServiceState.Ok;
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or OperationCanceledException or ServiceCallException)
        {
            return ServiceState.Down;
        }
    }
}
