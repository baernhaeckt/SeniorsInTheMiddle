using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Http.Features;
using Yarp.ReverseProxy.Forwarder;

sealed class ForwardProxy : IDisposable
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
        Uri? destination = GetDestinationUri(context);
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

    public void Dispose() => httpClient.Dispose();

    private static Uri? GetDestinationUri(HttpContext context)
    {
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (Uri.TryCreate(rawTarget, UriKind.Absolute, out var absoluteUri) &&
            (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
        {
            return absoluteUri;
        }

        if (!Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}",
                UriKind.Absolute, out var requestUri) ||
            (requestUri.Scheme != Uri.UriSchemeHttp && requestUri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return requestUri;
    }
}
