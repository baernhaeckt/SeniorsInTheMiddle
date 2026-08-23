using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using SeniorsInTheMiddle.Proxy.Telemetry;
using Yarp.ReverseProxy.Forwarder;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// The forwarding core: hands each request to YARP's <see cref="IHttpForwarder"/> with a
/// per-request <see cref="ForwardProxyTransformer"/> that anonymizes what goes upstream and
/// restores it on the way back, while emitting the telemetry the dashboard renders.
/// </summary>
sealed class ForwardProxy : IForwardProxy
{
    private readonly IHttpForwarder _forwarder;
    private readonly ITelemetrySink _telemetry;
    private readonly ClientLabeler _clientLabeler;
    private readonly IBodyMutationFactory _bodyMutations;
    private readonly BodyLimits _bodyLimits;
    private readonly InspectionScope _scope;
    private readonly Detours _detours;
    private readonly ILogger<ForwardProxyTransformer> _transformerLogger;
    private readonly PrivacyAssessor _privacy;

    private readonly UpstreamHttpClient _upstream;

    private readonly ForwarderRequestConfig _requestConfig = new()
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
        Detours detours,
        UpstreamHttpClient upstream,
        ILogger<ForwardProxyTransformer> transformerLogger,
        PrivacyAssessor privacy)
    {
        _forwarder = forwarder;
        _telemetry = telemetry;
        _clientLabeler = clientLabeler;
        _bodyMutations = bodyMutations;
        _bodyLimits = bodyLimits;
        _scope = scope;
        _detours = detours;
        _upstream = upstream;
        _transformerLogger = transformerLogger;
        _privacy = privacy;
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

        ExchangeTrace trace = new(_telemetry, CorrelationIds.NextRequest(), Facts(context, destination), _privacy);
        long startedAt = Stopwatch.GetTimestamp();

        // Answered here and never forwarded -- see Detours. The trace still sees it, so the
        // dashboard shows the request and its reason says where it went.
        if (_detours.For(destination) is { } detour)
        {
            trace.Passthrough($"detoured to {detour.Host}");

            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = detour.ToString();
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.CompleteAsync();

            trace.Completed(
                context.Response.StatusCode,
                0,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return;
        }

        // Counted on the way out rather than read from Content-Length, which a chunked
        // response does not carry -- see CountingStream.
        Stream responseBody = context.Response.Body;
        CountingStream counted = new(responseBody);
        context.Response.Body = counted;

        ForwarderError error;
        try
        {
            error = await _forwarder.SendAsync(
                context,
                destination.GetLeftPart(UriPartial.Authority),
                _upstream,
                _requestConfig,
                new ForwardProxyTransformer(
                    destination,
                    _bodyMutations.CreateForExchange(ClientIdentity.Of(context), destination, trace),
                    _bodyLimits,
                    _scope,
                    _transformerLogger,
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
            _clientLabeler.Label(context.Connection.RemoteIpAddress, context.Request.Headers.UserAgent),
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
