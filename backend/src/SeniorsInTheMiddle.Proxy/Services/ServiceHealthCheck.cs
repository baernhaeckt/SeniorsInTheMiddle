using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SeniorsInTheMiddle.Proxy.Services;

/// <summary>
/// Pings every configured python service. A configured service that does not answer makes
/// the check Unhealthy; a service with no socket path is reported as disabled and does not
/// affect the status, so a dev box without the container still reads Healthy.
/// </summary>
sealed class ServiceHealthCheck(ServiceConnections services) : IHealthCheck
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Pinged together, so the probe waits for the slowest service rather than for the sum.
        (string Name, string State, bool Failed)[] results = await Task.WhenAll(
            services.All.Select(service => ProbeAsync(service, cancellationToken)));

        Dictionary<string, object> data = results.ToDictionary(result => result.Name, result => (object)result.State);
        string[] failed = [.. results.Where(result => result.Failed).Select(result => result.Name)];

        return failed.Length == 0
            ? HealthCheckResult.Healthy("All configured services answer.", data)
            : HealthCheckResult.Unhealthy($"Not answering: {string.Join(", ", failed)}.", data: data);
    }

    private static async Task<(string Name, string State, bool Failed)> ProbeAsync(
        ServiceConnection service,
        CancellationToken cancellationToken)
    {
        if (!service.IsConfigured)
            return (service.Name, "disabled", false);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PingTimeout);
        try
        {
            await service.PingAsync(timeout.Token);
            return (service.Name, $"ok ({service.Options.SocketPath})", false);
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or OperationCanceledException or ServiceCallException)
        {
            return (service.Name, $"unreachable ({service.Options.SocketPath}): {ex.Message}", true);
        }
    }
}
