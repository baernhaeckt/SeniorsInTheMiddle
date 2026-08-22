namespace SeniorsInTheMiddle.Proxy.Telemetry;

public static class Registrar
{
    public static IServiceCollection AddTelemetryServices(this IServiceCollection services)
    {
        services.AddSignalR();

        services
            .AddSingleton<TelemetryDescriptor>()
            .AddSingleton<ClientLabeler>();

        // One pump, reached two ways. Registering the type twice would build two of them
        // and leave the sink's queue with nobody draining it.
        services.AddSingleton<TelemetryPump>();
        services.AddSingleton<ITelemetrySink>(provider => provider.GetRequiredService<TelemetryPump>());
        services.AddHostedService(provider => provider.GetRequiredService<TelemetryPump>());

        return services;
    }

    /// <summary>
    /// Has to run before the hub endpoint, so a refused origin never reaches the upgrade.
    /// </summary>
    public static WebApplication UseTelemetryOriginGuard(this WebApplication app)
    {
        app.UseMiddleware<TelemetryOriginGuard>();

        return app;
    }

    /// <summary>
    /// The stream carries decrypted request bodies, including the personal data found in
    /// them, so it takes a signed-in user. The fallback policy would cover this on its own;
    /// it is spelled out here because this is the endpoint a reader checks.
    ///
    /// A browser sends the token in the handshake's query string — see the
    /// <c>OnMessageReceived</c> hook in <see cref="InfrastructureRegistrations"/>.
    /// </summary>
    public static void MapTelemetryHub(this IEndpointRouteBuilder routes)
        => routes.MapHub<TelemetryHub>(TelemetryRoutes.HubPath).RequireAuthorization();
}
