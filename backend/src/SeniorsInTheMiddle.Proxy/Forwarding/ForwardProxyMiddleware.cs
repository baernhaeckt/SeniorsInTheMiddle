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
    private readonly RequestDelegate next;
    private readonly IForwardProxy proxy;
    private readonly SelfHostNames selfHostNames;
    private readonly ProxyPorts ports;

    public ForwardProxyMiddleware(
        RequestDelegate next,
        IForwardProxy proxy,
        SelfHostNames selfHostNames,
        ProxyPorts ports)
    {
        this.next = next;
        this.proxy = proxy;
        this.selfHostNames = selfHostNames;
        this.ports = ports;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!ports.IsProxyListener(context.Connection.LocalPort))
            return next(context);

        Uri? destination = ForwardProxy.GetProxyDestination(context);

        return destination is null || PointsAtUs(context, destination)
            ? next(context)
            : proxy.HandleAsync(context);
    }

    /// <summary>
    /// A device configured to use us as its proxy sends absolute form for this app's own
    /// endpoints too. Answering those locally keeps the app from proxying to itself.
    /// </summary>
    private bool PointsAtUs(HttpContext context, Uri destination)
        => destination.Port == context.Connection.LocalPort
           && selfHostNames.Contains(destination.Host);
}
