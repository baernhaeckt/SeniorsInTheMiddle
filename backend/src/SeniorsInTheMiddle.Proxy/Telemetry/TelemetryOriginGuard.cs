namespace SeniorsInTheMiddle.Proxy.Telemetry;

/// <summary>
/// Keeps other people's web pages off the telemetry stream.
///
/// CORS does not help here. A browser applies neither a preflight nor an origin check to a
/// WebSocket handshake, and the dashboard connects with negotiation skipped, so no
/// credentialed XHR ever reaches <c>UseCors</c>. Without this, any page a viewer happens to
/// visit could open the hub and read decrypted request bodies off it — the port is
/// externally reachable in the deployed setup.
///
/// A request with no Origin header is not a browser, so it passes: that is the .NET client
/// the tests use, and curl. A browser always sends one.
/// </summary>
sealed class TelemetryOriginGuard
{
    private readonly RequestDelegate next;
    private readonly HashSet<string> allowed;
    private readonly ILogger<TelemetryOriginGuard> logger;

    public TelemetryOriginGuard(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<TelemetryOriginGuard> logger)
    {
        this.next = next;
        this.logger = logger;
        allowed = new HashSet<string>(
            InfrastructureRegistrations.AllowedOrigins(configuration),
            StringComparer.OrdinalIgnoreCase);
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(TelemetryRoutes.HubPath))
            return next(context);

        string? origin = context.Request.Headers.Origin.ToString().TrimEnd('/');
        if (string.IsNullOrEmpty(origin) || allowed.Contains(origin))
            return next(context);

        logger.LogWarning(
            "Refused a telemetry connection from {Origin}. Add it to Cors:AllowedOrigins if "
            + "that is where the dashboard is served from.",
            origin);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
