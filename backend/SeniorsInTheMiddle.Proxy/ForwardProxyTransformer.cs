using Yarp.ReverseProxy.Forwarder;

sealed class ForwardProxyTransformer(Uri destination) : HttpTransformer
{
    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
        proxyRequest.RequestUri = destination;
        proxyRequest.Headers.Host = null;
    }
}
