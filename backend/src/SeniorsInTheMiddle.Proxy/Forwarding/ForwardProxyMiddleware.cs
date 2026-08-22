namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Forwards proxy traffic, and only on the proxy listeners.
///
/// A proxy client sends its request line in absolute form
/// ("GET http://example.com/ HTTP/1.1"), so those go to the forwarder. Origin-form
/// requests ("GET /api/v1/... ") fall through to the rest of the pipeline.
///
/// The API port forwards nothing at all. A stray absolute-form request there is answered
/// as an ordinary request, which keeps the dashboard's own port from acting as an open
/// proxy.
/// </summary>
sealed class ForwardProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IForwardProxy _proxy;
    private readonly SelfHostNames _selfHostNames;
    private readonly ProxyPorts _ports;

    public ForwardProxyMiddleware(
        RequestDelegate next,
        IForwardProxy proxy,
        SelfHostNames selfHostNames,
        ProxyPorts ports)
    {
        _next = next;
        _proxy = proxy;
        _selfHostNames = selfHostNames;
        _ports = ports;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!_ports.IsProxyListener(context.Connection.LocalPort))
            return _next(context);

        Uri? destination = ForwardProxy.GetProxyDestination(context);

        return destination is null || PointsAtUs(context, destination)
            ? _next(context)
            : _proxy.HandleAsync(context);
    }

    /// <summary>
    /// A device configured to use us as its proxy sends absolute form for this app's own
    /// endpoints too. Answering those locally keeps the app from proxying to itself.
    /// </summary>
    private bool PointsAtUs(HttpContext context, Uri destination)
        => destination.Port == context.Connection.LocalPort
           && _selfHostNames.Contains(destination.Host);
}
