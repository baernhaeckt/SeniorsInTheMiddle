namespace SeniorsInTheMiddle.Proxy.Telemetry;

/// <summary>
/// Keeps other people's web pages off the telemetry stream.
///
/// CORS does not help here. A browser applies neither a preflight nor an origin check to a
/// WebSocket handshake, and the dashboard connects with negotiation skipped, so no
/// credentialed XHR ever reaches <c>UseCors</c>. Without this, a page a signed-in viewer
/// happens to visit could open the hub from their browser — carrying their session with it —
/// and read decrypted request bodies off it. The port is externally reachable in the
/// deployed setup.
///
/// A request with no Origin header is not a browser, so it passes: that is the .NET client
/// the tests use, and curl. A browser always sends one. That used to be the whole story and
/// it left the stream open to anything that was not a browser; the hub now also requires a
/// signed-in user (see <c>MapTelemetryHub</c>), so this guard is the browser half of two
/// checks rather than the only one.
/// </summary>
sealed class TelemetryOriginGuard
{
    private readonly RequestDelegate _next;
    private readonly AllowedOrigins _allowed;
    private readonly ILogger<TelemetryOriginGuard> _logger;

    public TelemetryOriginGuard(
        RequestDelegate next,
        AllowedOrigins allowed,
        ILogger<TelemetryOriginGuard> logger)
    {
        _next = next;
        _allowed = allowed;
        _logger = logger;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(TelemetryRoutes.HubPath))
            return _next(context);

        string? origin = context.Request.Headers.Origin.ToString().TrimEnd('/');
        if (string.IsNullOrEmpty(origin) || _allowed.Contains(origin))
            return _next(context);

        _logger.LogWarning(
            "Refused a telemetry connection from {Origin}. Add it to Cors:AllowedOrigins if "
            + "that is where the dashboard is served from.",
            origin);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
