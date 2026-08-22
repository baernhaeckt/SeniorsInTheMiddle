using System.Text.Json;

namespace SeniorsInTheMiddle.Proxy.Services;

/// <summary>
/// Connects to every configured service once at startup and logs its <c>$info</c>, so a
/// wrong socket path or a daemon that failed to load its model shows up in the container
/// log right away instead of on the first request. Runs in the background: the proxy
/// starts listening regardless, and the health check keeps reporting the real state.
/// </summary>
sealed class ServiceStartupProbe(ServiceConnections services, ILogger<ServiceStartupProbe> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (ServiceConnection service in services.All)
        {
            if (!service.IsConfigured)
            {
                logger.LogInformation("Service {Service} is disabled (no Services:{Service}:SocketPath)", service.Name, service.Name);
                continue;
            }

            try
            {
                JsonElement info = await service.InfoAsync(stoppingToken);
                logger.LogInformation(
                    "Service {Service} at {SocketPath} answered: {Info}",
                    service.Name,
                    service.Options.SocketPath,
                    ServiceSocketClient.Describe(info));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is ServiceUnavailableException or ServiceCallException)
            {
                logger.LogError(ex, "Service {Service} at {SocketPath} is not answering", service.Name, service.Options.SocketPath);
            }
        }
    }
}
