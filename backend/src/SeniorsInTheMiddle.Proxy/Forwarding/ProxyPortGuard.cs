namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Keeps the API off the proxy listeners.
///
/// A request that reaches the pipeline on a proxy port was not proxy traffic --
/// <see cref="ForwardProxyMiddleware"/> already took everything in absolute form. What is
/// left is origin form, which on a proxy port only ever means a device fetching what it
/// needs to be set up: the CA certificate and the PAC file. Swagger, the WebAPI and the
/// telemetry stream answer on the API port alone, so a device configured to use the proxy
/// cannot reach them by accident.
/// </summary>
sealed class ProxyPortGuard
{
    /// <summary>The only paths a device needs before it trusts us. Kept in step with
    /// <see cref="Registrar.RegisterProxyEndpoints"/>.</summary>
    private static readonly string[] BootstrapPaths = ["/ca.crt", "/proxy.pac"];

    private readonly RequestDelegate _next;
    private readonly ProxyPorts _ports;

    public ProxyPortGuard(RequestDelegate next, ProxyPorts ports)
    {
        _next = next;
        _ports = ports;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!_ports.IsProxyListener(context.Connection.LocalPort) || IsBootstrapPath(context.Request.Path))
            return _next(context);

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    private static bool IsBootstrapPath(PathString path)
        => BootstrapPaths.Any(bootstrap => path.Equals(bootstrap, StringComparison.OrdinalIgnoreCase));
}
