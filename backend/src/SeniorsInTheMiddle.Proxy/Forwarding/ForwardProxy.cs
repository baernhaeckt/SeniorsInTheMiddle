using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using SeniorsInTheMiddle.Proxy.Telemetry;
using Yarp.ReverseProxy.Forwarder;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

sealed class ForwardProxy : IForwardProxy
{
    private readonly IHttpForwarder forwarder;
    private readonly ITelemetrySink telemetry;
    private readonly ClientLabeler clientLabeler;
    private readonly IBodyMutationFactory bodyMutations;
    private readonly BodyLimits bodyLimits;
    private readonly InspectionScope scope;
    private readonly ILogger<ForwardProxyTransformer> transformerLogger;

    private readonly UpstreamHttpClient upstream;

    private readonly ForwarderRequestConfig requestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(2)
    };

    public ForwardProxy(
        IHttpForwarder forwarder,
        ITelemetrySink telemetry,
        ClientLabeler clientLabeler,
        IBodyMutationFactory bodyMutations,
        BodyLimits bodyLimits,
        InspectionScope scope,
        UpstreamHttpClient upstream,
        ILogger<ForwardProxyTransformer> transformerLogger)
    {
        this.forwarder = forwarder;
        this.telemetry = telemetry;
        this.clientLabeler = clientLabeler;
        this.bodyMutations = bodyMutations;
        this.bodyLimits = bodyLimits;
        this.scope = scope;
        this.upstream = upstream;
        this.transformerLogger = transformerLogger;
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

        ExchangeTrace trace = new(telemetry, CorrelationIds.NextRequest(), Facts(context, destination));
        long startedAt = Stopwatch.GetTimestamp();

        // Counted on the way out rather than read from Content-Length, which a chunked
        // response does not carry -- see CountingStream.
        Stream responseBody = context.Response.Body;
        CountingStream counted = new(responseBody);
        context.Response.Body = counted;

        ForwarderError error;
        try
        {
            error = await forwarder.SendAsync(
                context,
                destination.GetLeftPart(UriPartial.Authority),
                upstream,
                requestConfig,
                new ForwardProxyTransformer(
                    destination,
                    bodyMutations.CreateForExchange(destination, trace),
                    bodyLimits,
                    scope,
                    transformerLogger,
                    trace));
        }
        finally
        {
            context.Response.Body = responseBody;
        }

        if (error != ForwarderError.None && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }

        trace.Completed(
            context.Response.StatusCode,
            counted.BytesWritten,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    /// <summary>
    /// What is known about a request before its body is read. The announcement itself is the
    /// trace's to make, once it knows how the body was treated.
    /// </summary>
    private RequestFacts Facts(HttpContext context, Uri destination)
        => new(
            ClientLabeler.Ip(context.Connection.RemoteIpAddress),
            clientLabeler.Label(context.Connection.RemoteIpAddress, context.Request.Headers.UserAgent),
            context.Request.Method,
            destination.Scheme == Uri.UriSchemeHttps ? TelemetryScheme.Https : TelemetryScheme.Http,
            destination.Host,
            destination.PathAndQuery,
            context.Request.ContentType,
            context.Request.ContentLength ?? 0);

    /// <summary>
    /// Where a request is meant to go, or null when it is not proxy traffic at all.
    ///
    /// A proxy client says so in two different ways depending on the scheme. Plain HTTP goes
    /// out in absolute form ("GET http://example.com/ HTTP/1.1"), and origin form on the same
    /// port belongs to the API and the telemetry stream, so it is deliberately not proxied.
    /// Inside an intercepted TLS tunnel the client believes it reached the origin server, so
    /// it sends origin form and the authority comes from the CONNECT that opened the tunnel.
    /// </summary>
    internal static Uri? GetProxyDestination(HttpContext context)
    {
        string? rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;

        // A tunnelled client may still send absolute form, so the origin-form check decides
        // which of the two applies rather than the presence of the tunnel alone.
        if (context.Features.Get<IInterceptedTunnel>() is { } tunnel && rawTarget is ['/', ..])
        {
            return Uri.TryCreate($"https://{tunnel.Authority}{rawTarget}", UriKind.Absolute, out Uri? tunnelled)
                ? tunnelled
                : null;
        }

        return Uri.TryCreate(rawTarget, UriKind.Absolute, out Uri? absoluteUri) &&
               (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps)
            ? absoluteUri
            : null;
    }
}
