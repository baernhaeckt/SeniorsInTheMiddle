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
        Dictionary<string, object> data = new();
        List<string> failed = [];

        foreach (ServiceConnection service in services.All)
        {
            if (!service.IsConfigured)
            {
                data[service.Name] = "disabled";
                continue;
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PingTimeout);
            try
            {
                await service.PingAsync(timeout.Token);
                data[service.Name] = $"ok ({service.Options.SocketPath})";
            }
            catch (Exception ex) when (ex is ServiceUnavailableException or OperationCanceledException or ServiceCallException)
            {
                data[service.Name] = $"unreachable ({service.Options.SocketPath}): {ex.Message}";
                failed.Add(service.Name);
            }
        }

        return failed.Count == 0
            ? HealthCheckResult.Healthy("All configured services answer.", data)
            : HealthCheckResult.Unhealthy($"Not answering: {string.Join(", ", failed)}.", data: data);
    }
}
