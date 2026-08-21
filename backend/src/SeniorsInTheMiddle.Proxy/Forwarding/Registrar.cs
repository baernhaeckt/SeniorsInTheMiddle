using System.Security.Cryptography.X509Certificates;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

public static class Registrar
{
    /// <summary>How long this app's own HTTPS certificate stays valid.</summary>
    private static readonly TimeSpan DashboardCertificateLifetime = TimeSpan.FromDays(365);

    public static IServiceCollection AddForwardProxyServices(this IServiceCollection services)
    {
        services
            .AddHttpForwarder()
            .AddSingleton<SelfHostNames>()
            .AddSingleton<IForwardProxy, ForwardProxy>()
            .AddSingleton<MitmCertificateProvider>()
            .AddSingleton<IStreamProxyFactory, StreamProxyFactory>()
            .AddSingleton<ConnectProxyMiddleware>();

        return services;
    }

    /// <summary>
    /// Two listeners on one process.
    ///
    /// The HTTP port carries proxy traffic: every connection is sniffed for a CONNECT
    /// request line, which becomes an intercepted TLS tunnel, and anything else falls
    /// through to the HTTP pipeline, where absolute-form requests are forwarded and
    /// origin-form requests reach the API.
    ///
    /// The HTTPS port serves the API and the telemetry stream only.
    /// Its certificate is signed by our own CA, so a device that already trusts
    /// /ca.crt for interception reaches the API without a second warning.
    /// </summary>
    public static IWebHostBuilder ConfigureProxyKestrel(
        this IWebHostBuilder webHost,
        IConfiguration configuration)
    {
        int httpPort = configuration.GetValue("Proxy:HttpPort", 8080);
        int httpsPort = configuration.GetValue("Proxy:HttpsPort", 8443);

        webHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(httpPort, listen => listen.Use(
                next => connection => listen
                    .ApplicationServices
                    .GetRequiredService<ConnectProxyMiddleware>()
                    .InvokeAsync(connection, next)));

            // A port of 0 or less turns the HTTPS endpoint off, for hosts that
            // terminate TLS themselves and only forward plain HTTP.
            if (httpsPort <= 0)
                return;

            X509Certificate2 certificate = options.ApplicationServices
                .GetRequiredService<MitmCertificateProvider>()
                .CreateServerCertificate(
                    options.ApplicationServices.GetRequiredService<SelfHostNames>().Names,
                    DashboardCertificateLifetime);

            options.ListenAnyIP(httpsPort, listen => listen.UseHttps(certificate));
        });

        return webHost;
    }

    public static WebApplication UseForwardProxy(this WebApplication app)
    {
        // Create the MITM CA before accepting requests so its public certificate is
        // available to clients from the first request onwards.
        app.Services.GetRequiredService<MitmCertificateProvider>();

        app.UseMiddleware<ForwardProxyMiddleware>();

        return app;
    }

    public static void RegisterProxyEndpoints(this IEndpointRouteBuilder routes)
    {
        // The root certificate a device must trust before HTTPS can be read. The setup
        // guide in the SPA links here.
        routes.MapGet("/ca.crt", (MitmCertificateProvider certificates) => Results.File(
                certificates.PublicCertificate,
                "application/x-x509-ca-cert",
                "sitm-ca.crt"))
            .WithName("DownloadCaCertificate")
            .WithTags("Proxy");

        // Auto-configuration, for devices that take a PAC URL instead of host and port.
        // The address is taken from the request, so the file describes whichever name
        // the device actually reached us by.
        routes.MapGet("/proxy.pac", (HttpContext context, IConfiguration configuration) =>
            {
                string host = context.Request.Host.Host;
                int port = configuration.GetValue("Proxy:HttpPort", 8080);

                return Results.Text(
                    $$"""
                    function FindProxyForURL(url, host) {
                      if (isPlainHostName(host) || shExpMatch(host, "*.local") ||
                          isInNet(host, "127.0.0.0", "255.0.0.0")) {
                        return "DIRECT";
                      }
                      return "PROXY {{host}}:{{port}}";
                    }
                    """,
                    "application/x-ns-proxy-autoconfig");
            })
            .WithName("ProxyAutoConfiguration")
            .WithTags("Proxy");
    }
}
