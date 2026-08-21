using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Yarp.ReverseProxy.Forwarder;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

sealed class ForwardProxy : IDisposable, IForwardProxy
{
    private readonly IHttpForwarder forwarder;

    private readonly HttpMessageInvoker httpClient;

    private readonly ForwarderRequestConfig requestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(2)
    };

    public ForwardProxy(IHttpForwarder forwarder)
    {
        this.forwarder = forwarder;
        httpClient = new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        });

    }

    public async Task HandleAsync(HttpContext context)
    {
        Uri? destination = GetProxyDestination(context);
        if (destination is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("The proxy request must contain a valid destination URI.");
            return;
        }

        ForwarderError error = await forwarder.SendAsync(
            context,
            destination.GetLeftPart(UriPartial.Authority),
            httpClient,
            requestConfig,
            new ForwardProxyTransformer(destination));

        if (error != ForwarderError.None && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    public void Dispose()
        => httpClient.Dispose();

    /// <summary>
    /// The destination of an explicit proxy request, or null when the request line is in
    /// origin form. Origin form belongs to the API and the telemetry stream on this same port,
    /// so it is deliberately not treated as proxy traffic.
    /// </summary>
    internal static Uri? GetProxyDestination(HttpContext context)
    {
        string? rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;

        return Uri.TryCreate(rawTarget, UriKind.Absolute, out Uri? absoluteUri) &&
               (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps)
            ? absoluteUri
            : null;
    }
}
