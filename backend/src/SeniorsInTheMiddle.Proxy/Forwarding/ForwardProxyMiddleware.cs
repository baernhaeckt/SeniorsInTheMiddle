namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Splits proxy traffic from API traffic on the shared HTTP listener.
///
/// A proxy client sends its request line in absolute form
/// ("GET http://example.com/ HTTP/1.1"), so those go to the forwarder. The API
/// and the telemetry stream arrive in origin form ("GET /api/v1/... ") and
/// fall through to the rest of the pipeline.
/// </summary>
sealed class ForwardProxyMiddleware
{
    private readonly RequestDelegate next;
    private readonly IForwardProxy proxy;
    private readonly SelfHostNames selfHostNames;

    public ForwardProxyMiddleware(RequestDelegate next, IForwardProxy proxy, SelfHostNames selfHostNames)
    {
        this.next = next;
        this.proxy = proxy;
        this.selfHostNames = selfHostNames;
    }

    public Task InvokeAsync(HttpContext context)
    {
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
