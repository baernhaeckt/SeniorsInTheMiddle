using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using SeniorsInTheMiddle.Proxy.Forwarding;
using SeniorsInTheMiddle.Proxy.Services.PrivacyCheck;
using SeniorsInTheMiddle.Proxy.Telemetry;

namespace Backend.Tests.Integration;

/// <summary>
/// The real CONNECT path, end to end, over real sockets: a client that treats the proxy as an
/// HTTP proxy, the proxy's own <see cref="ConnectProxyMiddleware"/> and
/// <see cref="ForwardProxyMiddleware"/>, and an HTTPS destination that records what arrived.
///
/// Nothing here is a stand-in except the destination's trust. Interception only works because
/// the client accepts a certificate minted by the proxy's own CA, and the proxy only reaches a
/// self-signed destination because <see cref="UpstreamHttpClient"/> is handed a permissive
/// handler; both of those are properties of the test, and everything between them is the
/// shipping code.
/// </summary>
internal sealed class TunnelHarness : IAsyncDisposable
{
    /// <summary>
    /// One CA for the whole test run.
    ///
    /// Generating it costs a 4096-bit key, and <see cref="MitmCertificateProvider"/> writes it
    /// to disk on first use, so a per-harness path would pay that cost per test and a shared
    /// default path would race two tests writing the same file. Creating it once behind a Lazy
    /// avoids both.
    /// </summary>
    private static readonly Lazy<string> SharedCertificateAuthorityPath = new(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "sitm-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);

        return Path.Combine(directory, "mitm-ca.pfx");
    });

    /// <summary>Everything the harness records about a run, shared between the destination
    /// server, the proxy and the assertions.</summary>
    private sealed class HarnessState
    {
        public RecordedRequest? Received;

        public readonly ConcurrentQueue<TelemetryEvent> Telemetry = new();

        public readonly ConcurrentQueue<string> Logs = new();

        /// <summary>Issuers of the certificates the client was offered, so a test can show the
        /// connection really was intercepted rather than passed through.</summary>
        public readonly ConcurrentQueue<string> PresentedIssuers = new();
    }

    /// <summary>Keeps the proxy's own log lines so a test can tell which branch a tunnel took.</summary>
    private sealed class QueueLoggerProvider(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new QueueLogger(lines);

        public void Dispose()
        {
        }

        /// <summary>Enqueues every formatted message, at any level.</summary>
        private sealed class QueueLogger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => lines.Enqueue(formatter(state, exception));
        }
    }

    /// <summary>Collects published events so a test can assert on the sequence the proxy emitted.</summary>
    private sealed class QueueTelemetrySink(ConcurrentQueue<TelemetryEvent> events) : ITelemetrySink
    {
        public void Publish(TelemetryEvent telemetryEvent) => events.Enqueue(telemetryEvent);
    }

    private readonly WebApplication _destination;
    private readonly WebApplication _proxy;
    private readonly HarnessState _state;
    private readonly X509Certificate2 _destinationCertificate;

    private TunnelHarness(
        WebApplication destination,
        WebApplication proxy,
        HarnessState state,
        X509Certificate2 destinationCertificate,
        Uri destinationUri,
        Uri proxyUri,
        int unreachablePort)
    {
        _destination = destination;
        _proxy = proxy;
        _state = state;
        _destinationCertificate = destinationCertificate;
        DestinationUri = destinationUri;
        ProxyUri = proxyUri;
        UnreachablePort = unreachablePort;
    }

    /// <summary>The HTTPS origin server, e.g. <c>https://127.0.0.1:54321</c>.</summary>
    public Uri DestinationUri { get; }

    /// <summary>The proxy a client is pointed at.</summary>
    public Uri ProxyUri { get; }

    /// <summary>A port nothing listens on, for the tunnels that are meant to fail.</summary>
    public int UnreachablePort { get; }

    public RecordedRequest? Received => _state.Received;

    public IReadOnlyList<TelemetryEvent> Telemetry => _state.Telemetry.ToArray();

    public IReadOnlyList<string> Logs => _state.Logs.ToArray();

    public IReadOnlyList<string> PresentedIssuers => _state.PresentedIssuers.ToArray();

    public static async Task<TunnelHarness> StartAsync(
        IBodyMutationFactory? mutation = null,
        BodyLimits? limits = null,
        Func<HttpContext, byte[], Task>? respond = null)
    {
        HarnessState state = new();

        // The proxy's port has to be known before it starts, because ForwardProxyMiddleware
        // decides what is proxy traffic from the port the request arrived on.
        int proxyPort = FreePort();
        int apiPort = FreePort();
        int unreachablePort = FreePort();

        X509Certificate2 destinationCertificate = SelfSignedFor("127.0.0.1");
        WebApplication destinationApp = BuildDestination(state, destinationCertificate, respond);
        await destinationApp.StartAsync();
        Uri destinationUri = new(destinationApp.Urls.First().Replace("[::1]", "127.0.0.1"));

        WebApplication proxyApp = BuildProxy(state, proxyPort, apiPort, destinationCertificate, mutation, limits);
        await proxyApp.StartAsync();

        return new TunnelHarness(
            destinationApp,
            proxyApp,
            state,
            destinationCertificate,
            destinationUri,
            new Uri($"http://127.0.0.1:{proxyPort}"),
            unreachablePort);
    }

    /// <summary>
    /// A client pointed at the proxy, which therefore opens a CONNECT tunnel for every https
    /// request. It accepts whatever certificate it is offered and records the issuer.
    /// </summary>
    public HttpClient CreateProxiedClient() => new(new SocketsHttpHandler
    {
        Proxy = new WebProxy(ProxyUri),
        UseProxy = true,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is X509Certificate2 presented)
                    _state.PresentedIssuers.Enqueue(presented.Issuer);

                return true;
            },
        },
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Opens a CONNECT tunnel by hand and completes the TLS handshake, for the cases that have
    /// to put something other than an HTTP request inside it.
    /// </summary>
    public async Task<(TcpClient Socket, SslStream Tls)> ConnectTunnelAsync(string authority)
    {
        TcpClient socket = new();
        await socket.ConnectAsync(ProxyUri.Host, ProxyUri.Port);

        NetworkStream network = socket.GetStream();
        await network.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
            $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n"));

        byte[] established = new byte[128];
        int read = await network.ReadAsync(established);
        string status = System.Text.Encoding.ASCII.GetString(established, 0, read);
        Assert.Contains("200", status, $"The proxy refused the tunnel: {status.Trim()}");

        SslStream tls = new(network, leaveInnerStreamOpen: true, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(authority.Split(':')[0]);

        return (socket, tls);
    }

    public RecordedRequest RequireReceived()
    {
        RecordedRequest? received = _state.Received;
        Assert.IsNotNull(received, "The destination server received no request at all.");

        return received;
    }

    public async ValueTask DisposeAsync()
    {
        await _proxy.StopAsync();
        await _destination.StopAsync();
        await _proxy.DisposeAsync();
        await _destination.DisposeAsync();
        _destinationCertificate.Dispose();
    }

    private static WebApplication BuildDestination(
        HarnessState state,
        X509Certificate2 certificate,
        Func<HttpContext, byte[], Task>? respond)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
        {
            // Pinned so ALPN cannot negotiate HTTP/2 to the origin. Chunked framing does not
            // exist in HTTP/2, and the framing is what these tests are about.
            listen.Protocols = HttpProtocols.Http1;
            listen.UseHttps(certificate);
        }));

        WebApplication app = builder.Build();
        app.Run(async context =>
        {
            using MemoryStream body = new();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);

            state.Received = new RecordedRequest(
                context.Request.Method,
                context.Request.Path + context.Request.QueryString,
                context.Request.Host.Value ?? string.Empty,
                context.Request.Headers.ToDictionary(
                    header => header.Key,
                    header => header.Value.Select(value => value ?? string.Empty).ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                body.ToArray());

            // The recorder has already drained the request, so the responder is handed what it
            // read rather than an empty stream.
            if (respond is not null)
                await respond(context, state.Received.Body);
            else
                await context.Response.WriteAsync("ok", context.RequestAborted);
        });

        return app;
    }

    private static WebApplication BuildProxy(
        HarnessState state,
        int proxyPort,
        int apiPort,
        X509Certificate2 destinationCertificate,
        IBodyMutationFactory? mutation,
        BodyLimits? limits)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(new QueueLoggerProvider(state.Logs));

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mitm:CertificatePath"] = SharedCertificateAuthorityPath.Value,

            // The proxy's own appsettings.json travels into the test output and pins the level
            // to Information, which is above the line the tunnel decisions are logged at.
            ["Logging:LogLevel:Default"] = "Debug",
        });

        builder.Services
            .AddHttpForwarder()
            .AddSingleton(new ProxyPorts(HttpProxy: proxyPort, HttpsProxy: 0, Api: apiPort))
            .AddSingleton(limits ?? new BodyLimits(BodyLimits.DefaultMaxMutableBodyBytes))
            .AddSingleton<IBodyMutationFactory>(mutation ?? new PassthroughMutationFactory())
            .AddSingleton<SelfHostNames>()
            .AddSingleton<InterceptionBypass>()
            .AddSingleton<InspectionScope>()
            .AddSingleton<Detours>()
            .AddSingleton<MitmCertificateProvider>()
            .AddSingleton<IStreamProxyFactory, StreamProxyFactory>()
            .AddSingleton<ConnectProxyMiddleware>()
            .AddSingleton<ClientLabeler>()
            .AddSingleton<ITelemetrySink>(new QueueTelemetrySink(state.Telemetry))
            // Disabled: the assessor answers "skipped" at once, and no tunnel test waits on it.
            .AddSingleton<IPrivacyCheckServiceClient, DisabledPrivacyCheck>()
            .AddSingleton<PrivacyAssessor>()
            .AddSingleton<IForwardProxy, ForwardProxy>();

        // The one seam in all of this: the destination is self-signed, so the process has no
        // reason to trust it and every other way of arranging that is worse.
        builder.Services.AddSingleton(TrustingUpstreamClient(destinationCertificate));

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, proxyPort, listen =>
        {
            // Both as in Registrar.ConfigureProxyKestrel: HTTP/1.1 only, and every connection
            // is offered to the CONNECT sniffing first.
            listen.Protocols = HttpProtocols.Http1;
            listen.Use(next => connection => listen
                .ApplicationServices
                .GetRequiredService<ConnectProxyMiddleware>()
                .InvokeAsync(connection, next));
        }));

        WebApplication app = builder.Build();
        app.UseMiddleware<ForwardProxyMiddleware>();
        app.UseMiddleware<ProxyPortGuard>();

        // Stands in for the bootstrap endpoints, to show a tunnelled request aimed at the
        // proxy itself is still answered locally rather than forwarded back to us.
        app.MapGet("/ca.crt", () => Results.Text("certificate", "application/x-x509-ca-cert"));

        return app;
    }

    private static UpstreamHttpClient TrustingUpstreamClient(X509Certificate2 destinationCertificate)
    {
        SocketsHttpHandler handler = UpstreamHttpClient.CreateHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && certificate.GetCertHashString() == destinationCertificate.GetCertHashString();

        return new UpstreamHttpClient(handler);
    }

    private static X509Certificate2 SelfSignedFor(string address)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new($"CN={address}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder names = new();
        names.AddIpAddress(IPAddress.Parse(address));
        request.CertificateExtensions.Add(names.Build());

        using X509Certificate2 generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A port that was free a moment ago. Racy in principle, and the alternative is
    /// starting the proxy before it can be told which port it is on.</summary>
    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}

/// <summary>A privacy check that is switched off, as on a dev box without the container.</summary>
internal sealed class DisabledPrivacyCheck : IPrivacyCheckServiceClient
{
    public bool IsEnabled => false;

    public Task<PrivacyRiskResult> RiskCheckAsync(string text, IReadOnlyList<string> replacedNames, CancellationToken cancellationToken = default)
        => Task.FromResult(PrivacyRiskResult.Empty);
}
