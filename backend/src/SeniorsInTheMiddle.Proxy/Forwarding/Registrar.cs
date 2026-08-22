using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

public static class Registrar
{
    /// <summary>How long this app's own TLS certificate stays valid.</summary>
    private static readonly TimeSpan ProxyCertificateLifetime = TimeSpan.FromDays(365);

    public static IServiceCollection AddForwardProxyServices(this IServiceCollection services)
    {
        // The rewrite applied to every proxied body, request and response, plaintext and
        // intercepted HTTPS alike. Replacing this one registration is the whole of "start
        // redacting" -- see IBodyMutationFactory.
        services.AddSingleton<IBodyMutationFactory, PassthroughMutationFactory>();

        services
            .AddHttpForwarder()
            .AddSingleton(provider => ProxyPorts.From(provider.GetRequiredService<IConfiguration>()))
            .AddSingleton(provider => BodyLimits.From(provider.GetRequiredService<IConfiguration>()))
            .AddSingleton<SelfHostNames>()
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
        ProxyPorts ports = ProxyPorts.From(configuration);

        webHost.ConfigureKestrel(options =>
        {
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
