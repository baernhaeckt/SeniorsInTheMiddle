using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.AspNetCore.Server.Kestrel.Core;
using SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Composition root for the forwarding stack: the DI registrations, and the Kestrel listeners
/// that give the process its proxy and API ports.
/// </summary>
public static class Registrar
{
    /// <summary>How long this app's own TLS certificate stays valid.</summary>
    private static readonly TimeSpan ProxyCertificateLifetime = TimeSpan.FromDays(365);

    public static IServiceCollection AddForwardProxyServices(this IServiceCollection services)
    {
        services
            .AddHttpForwarder()
            .AddSingleton(provider => ProxyPorts.From(provider.GetRequiredService<IConfiguration>()))
            .AddSingleton(provider => BodyLimits.From(provider.GetRequiredService<IConfiguration>()))
            .AddSingleton(provider => VaultLifetime.From(provider.GetRequiredService<IConfiguration>()))
            .AddSingleton<TokenDetectionService>()
            // Where a client's stand-in map lives between the request that fills it and the
            // later request whose response needs it -- see AnonymizerVault. Its clock is
            // registered rather than taken from the default so a test can expire an entry
            // without waiting two days for it.
            .AddSingleton(TimeProvider.System)
            .AddSingleton<AnonymizerVault>()
            // The rewrite applied to every proxied body, request and response, plaintext and
            // intercepted HTTPS alike. PassthroughMutationFactory is the one that changes
            // nothing, for a deployment that only watches -- see IBodyMutationFactory.
            .AddSingleton<IBodyMutationFactory, ReplacerService>()
            .AddSingleton<PrivacyAssessor>()
            .AddSingleton<SelfHostNames>()
            .AddSingleton<InterceptionBypass>()
            .AddSingleton<InspectionScope>()
            .AddSingleton<UpstreamHttpClient>()
            .AddSingleton<IForwardProxy, ForwardProxy>()
            .AddSingleton<MitmCertificateProvider>()
            .AddSingleton<IStreamProxyFactory, StreamProxyFactory>()
            .AddSingleton<ConnectProxyMiddleware>();

        return services;
    }

    /// <summary>
    /// Three listeners on one process, one role each -- see <see cref="ProxyPorts"/>.
    ///
    /// Both proxy ports run the same connection middleware: every connection is sniffed
    /// for a CONNECT request line, which becomes an intercepted TLS tunnel, and anything
    /// else falls through to the HTTP pipeline, where absolute-form requests are forwarded
    /// and origin-form requests get the CA certificate or the PAC file.
    ///
    /// The difference between them is only the transport. On the HTTPS proxy port TLS is
    /// terminated first, so the sniffing sees the same plaintext request line either way.
    /// Its certificate is signed by our own CA, so a device that already trusts /ca.crt for
    /// interception reaches it without a second warning.
    ///
    /// The API port carries the WebAPI, Swagger and the telemetry stream, and never proxies.
    /// </summary>
    public static IWebHostBuilder ConfigureProxyKestrel(
        this IWebHostBuilder webHost,
        IConfiguration configuration)
    {
        webHost.ConfigureKestrel(options =>
        {
            // The same instance the rest of the app resolves, parsed and validated once.
            ProxyPorts ports = options.ApplicationServices.GetRequiredService<ProxyPorts>();

            // Pass header bytes through instead of insisting they are ASCII.
            //
            // A proxy does not get to choose what a destination puts in a header. Real sites send raw UTF-8 in
            // Content-Disposition filenames (an umlaut in an image name is enough), which is not legal HTTP but
            // is common. HttpClient hands those to us decoded as Latin-1 -- one char per byte, so "ö" arrives as
            // "Ã¶" -- and Kestrel then refuses to write a non-ASCII header back, aborting the whole response.
            // The client sees a 502 for a resource the destination served with 200: on a product page, an image
            // that silently fails to load.
            //
            // Selecting Latin-1 for writing turns each of those chars back into the byte it came from, so the
            // header reaches the client exactly as the destination sent it. Round-tripping the bytes is the only
            // behaviour a transparent proxy can defend; validating them is the destination's business, not ours.
            //
            // Response direction only. No client has been seen sending such a header, and the request direction
            // is not symmetrical: Kestrel decodes there rather than encodes, so changing it would reinterpret
            // what clients send rather than pass through what a destination sent.
            //
            // Nothing else changes: Latin-1 and ASCII agree byte-for-byte below U+0080, and these chars come
            // from a Latin-1 decode, so none of them can exceed U+00FF and none is unmappable.
            options.ResponseHeaderEncodingSelector = _ => Encoding.Latin1;

            options.ListenAnyIP(ports.HttpProxy, listen =>
            {
                PinToHttp11(listen);
                UseConnectSniffing(listen);
            });

            options.ListenAnyIP(ports.Api);

            // A port of 0 or less turns the TLS proxy listener off, for deployments whose
            // clients only ever speak plain HTTP to the proxy.
            if (ports.HttpsProxy <= 0)
                return;

            X509Certificate2 certificate = options.ApplicationServices
                .GetRequiredService<MitmCertificateProvider>()
                .CreateServerCertificate(
                    options.ApplicationServices.GetRequiredService<SelfHostNames>().Names,
                    ProxyCertificateLifetime);

            options.ListenAnyIP(ports.HttpsProxy, listen =>
            {
                // Pinned before UseHttps, so ALPN only ever advertises what the sniffing
                // below can read.
                PinToHttp11(listen);

                // Registration order is execution order, so TLS is terminated before the
                // request line is read.
                listen.UseHttps(certificate);
                UseConnectSniffing(listen);
            });
        });

        return webHost;
    }

    /// <summary>
    /// CONNECT is read as a plain-text request line, so a proxy listener speaks HTTP/1.1 and
    /// nothing else. Left at the default, ALPN could negotiate HTTP/2 on the TLS port and the
    /// request line would never appear in the form the sniffing expects.
    /// </summary>
    private static void PinToHttp11(ListenOptions listen) => listen.Protocols = HttpProtocols.Http1;

    /// <summary>Offers every connection on this listener to <see cref="ConnectProxyMiddleware"/> first.</summary>
    private static void UseConnectSniffing(ListenOptions listen)
        => listen.Use(next => connection => listen
            .ApplicationServices
            .GetRequiredService<ConnectProxyMiddleware>()
            .InvokeAsync(connection, next));

    public static WebApplication UseForwardProxy(this WebApplication app)
    {
        // Create the MITM CA before accepting requests so its public certificate is
        // available to clients from the first request onwards.
        app.Services.GetRequiredService<MitmCertificateProvider>();

        app.UseMiddleware<ForwardProxyMiddleware>();

        // Whatever the forwarder did not take is API traffic, and on a proxy port only the
        // two bootstrap endpoints are API traffic a device is entitled to.
        app.UseMiddleware<ProxyPortGuard>();

        return app;
    }

    /// <summary>
    /// Both endpoints are deliberately anonymous, against the app's deny-by-default policy.
    ///
    /// They are what a device fetches to start trusting the proxy at all, which happens long
    /// before anyone has an account or a browser pointed at the dashboard. Requiring a token
    /// here would mean a device could only be onboarded once it was already onboarded.
    /// Neither leaks anything private: the CA's public certificate is public by definition,
    /// and the PAC file only restates the address the caller already used to reach us.
    /// </summary>
    public static void RegisterProxyEndpoints(this IEndpointRouteBuilder routes)
    {
        // The root certificate a device must trust before HTTPS can be read. The setup
        // guide in the SPA links here.
        routes.MapGet("/ca.crt", (MitmCertificateProvider certificates) => Results.File(
                certificates.PublicCertificate,
                "application/x-x509-ca-cert",
                "sitm-ca.crt"))
            .WithName("DownloadCaCertificate")
            .WithTags("Proxy")
            .AllowAnonymous();

        // Auto-configuration, for devices that take a PAC URL instead of host and port.
        // The address is taken from the request, so the file describes whichever name
        // the device actually reached us by.
        routes.MapGet("/proxy.pac", (HttpContext context, ProxyPorts ports) =>
            {
                string host = context.Request.Host.Host;

                // A semicolon-separated list is tried left to right, so a client that
                // understands a TLS proxy uses one and the rest fall back to the plain port.
                string proxies = ports.HttpsProxy > 0
                    ? $"HTTPS {host}:{ports.HttpsProxy}; PROXY {host}:{ports.HttpProxy}"
                    : $"PROXY {host}:{ports.HttpProxy}";

                return Results.Text(
                    $$"""
                    function FindProxyForURL(url, host) {
                      if (isPlainHostName(host) || shExpMatch(host, "*.local") ||
                          isInNet(host, "127.0.0.0", "255.0.0.0")) {
                        return "DIRECT";
                      }
                      return "{{proxies}}";
                    }
                    """,
                    "application/x-ns-proxy-autoconfig");
            })
            .WithName("ProxyAutoConfiguration")
            .WithTags("Proxy")
            .AllowAnonymous();
    }
}
