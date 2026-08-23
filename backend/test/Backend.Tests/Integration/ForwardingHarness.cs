using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using SeniorsInTheMiddle.Proxy.Forwarding;
using SeniorsInTheMiddle.Proxy.Telemetry;

using Yarp.ReverseProxy.Forwarder;

using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace Backend.Tests.Integration;

/// <summary>
/// One request as the destination server saw it. Header names are compared
/// case-insensitively, the way they arrive on the wire.
/// </summary>
internal sealed record RecordedRequest(
    string Method,
    string Target,
    string Host,
    IReadOnlyDictionary<string, string[]> Headers,
    byte[] Body)
{
    public bool Has(string headerName) => Headers.ContainsKey(headerName);

    /// <summary>The header's values joined with ", ", or null when it was not sent.</summary>
    public string? Value(string headerName)
        => Headers.TryGetValue(headerName, out string[]? values) ? string.Join(", ", values) : null;
}

/// <summary>
/// A mutation defined inline by the test that uses it. Returning null from either half means
/// that body is forwarded exactly as it arrived.
///
/// A fresh exchange object is handed out per call, the way the contract says, so a test that
/// carries state from the request to the response is exercising the real arrangement.
/// </summary>
internal sealed class DelegateMutationFactory(
    Func<ReadOnlyMemory<byte>, BodyDescriptor, byte[]?>? onRequest = null,
    Func<ReadOnlyMemory<byte>, BodyDescriptor, byte[]?>? onResponse = null,
    Func<string, string>? onStreamChunk = null) : IBodyMutationFactory
{
    public bool Rewrites => onRequest is not null || onResponse is not null || onStreamChunk is not null;

    public IExchangeBodyMutation CreateForExchange(ClientIdentity client, Uri destination, IExchangeObserver observer)
        => new Exchange(onRequest, onResponse, onStreamChunk);

    /// <summary>Applies whichever callbacks the test supplied; a missing one leaves that half
    /// of the exchange untouched.</summary>
    private sealed class Exchange(
        Func<ReadOnlyMemory<byte>, BodyDescriptor, byte[]?>? onRequest,
        Func<ReadOnlyMemory<byte>, BodyDescriptor, byte[]?>? onResponse,
        Func<string, string>? onStreamChunk) : IExchangeBodyMutation
    {
        public ValueTask<byte[]?> MutateRequestAsync(
            ReadOnlyMemory<byte> body,
            BodyDescriptor descriptor,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(onRequest?.Invoke(body, descriptor));

        public ValueTask<byte[]?> MutateResponseAsync(
            ReadOnlyMemory<byte> body,
            BodyDescriptor descriptor,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(onResponse?.Invoke(body, descriptor));

        /// <summary>Null unless the test asked for a streaming rewrite, so every existing test
        /// keeps the "an event stream is not touched" behaviour it was written against.</summary>
        public IExchangeStreamMutation? CreateResponseStream(BodyDescriptor descriptor)
            => onStreamChunk is null ? null : new Stream(onStreamChunk);

        /// <summary>Holds nothing back: what a test hands over per chunk is what comes out.</summary>
        private sealed class Stream(Func<string, string> onChunk) : IExchangeStreamMutation
        {
            public string Mutate(string chunk) => onChunk(chunk);

            public string Flush() => string.Empty;
        }
    }
}

/// <summary>
/// The two things a body rewrite gets wrong, asserted against the bytes that really arrived
/// rather than against the headers the proxy meant to send.
/// </summary>
internal static class Framing
{
    /// <summary>
    /// A body is delimited by Content-Length or by Transfer-Encoding: chunked, never by both.
    /// A message carrying both is a request-smuggling vector, and the usual way to produce one
    /// is to rewrite a chunked body into a buffer and set a length beside the Transfer-Encoding
    /// that is still there.
    /// </summary>
    public static void IsUnambiguous(RecordedRequest received)
    {
        bool chunked = received.Value(HeaderNames.TransferEncoding)?
            .Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;

        Assert.IsFalse(
            received.Has(HeaderNames.ContentLength) && chunked,
            $"Both framings were sent: Content-Length: {received.Value(HeaderNames.ContentLength)} " +
            $"and Transfer-Encoding: {received.Value(HeaderNames.TransferEncoding)}.");
    }

    /// <summary>
    /// Content-Length counts bytes, so it has to match the body that arrived.
    ///
    /// A request that carries no length is chunked and consistent by definition, so it
    /// passes. Use <see cref="HasLength"/> where the point of the test is that a length was
    /// sent at all, or the assertion is satisfied by its own absence.
    /// </summary>
    public static void MatchesBody(RecordedRequest received)
    {
        string? declared = received.Value(HeaderNames.ContentLength);
        if (declared is null)
            return;

        Assert.AreEqual(
            received.Body.Length.ToString(),
            declared,
            "Content-Length does not describe the body that arrived.");
    }

    /// <summary>A Content-Length was sent, and it is the length of the body that arrived.</summary>
    public static void HasLength(RecordedRequest received)
    {
        Assert.IsTrue(
            received.Has(HeaderNames.ContentLength),
            "No Content-Length was sent, so the body went out chunked.");

        MatchesBody(received);
    }
}

/// <summary>
/// Two real Kestrel listeners on loopback: a destination that records what reached it, and a
/// forwarder in front of it that runs a <see cref="HttpTransformer"/> over every request.
///
/// Real sockets are the whole point. Request framing -- whether the body is delimited by
/// Content-Length or by Transfer-Encoding: chunked -- exists only on the wire, so an
/// in-memory TestServer cannot show it, and that framing is exactly what breaks when a proxy
/// rewrites a body without rewriting the headers that describe it.
///
/// The client is pointed at the forwarder as an HTTP proxy, so it sends its request line in
/// absolute form and <see cref="ForwardProxy.GetProxyDestination"/> sees what it sees in
/// production.
/// </summary>
internal sealed class ForwardingHarness : IAsyncDisposable
{
    /// <summary>
    /// Shared between the two request delegates and the harness. It exists so the delegates
    /// can be registered before the listeners hand out their ports, which is the only order
    /// in which the harness can know its own addresses.
    /// </summary>
    private sealed class HarnessState
    {
        public RecordedRequest? Received;

        public readonly Channel<ForwarderError> Completions = Channel.CreateUnbounded<ForwarderError>();

        public readonly RecordingLogger Logger = new();

        public readonly ConcurrentQueue<TelemetryEvent> Telemetry = new();
    }

    /// <summary>Collects published events so a test can assert on the sequence the proxy emitted.</summary>
    private sealed class QueueTelemetrySink(ConcurrentQueue<TelemetryEvent> events) : ITelemetrySink
    {
        public void Publish(TelemetryEvent telemetryEvent) => events.Enqueue(telemetryEvent);
    }

    /// <summary>
    /// Keeps the transform's own log lines, so a test can assert that a body it deliberately
    /// made ineligible was reported as skipped rather than quietly waved through.
    /// </summary>
    internal sealed class RecordingLogger : ILogger<ForwardProxyTransformer>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> WarningsAndAbove
        {
            get
            {
                lock (_entries)
                {
                    return _entries
                        .Where(entry => entry.Level >= LogLevel.Warning)
                        .Select(entry => entry.Message)
                        .ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private readonly WebApplication _destination;
    private readonly WebApplication _forwarder;
    private readonly HttpMessageInvoker _upstream;
    private readonly HarnessState _state;

    private ForwardingHarness(
        WebApplication destination,
        WebApplication forwarder,
        HttpMessageInvoker upstream,
        HarnessState state,
        Uri destinationUri,
        Uri proxyUri)
    {
        _destination = destination;
        _forwarder = forwarder;
        _upstream = upstream;
        _state = state;
        DestinationUri = destinationUri;
        ProxyUri = proxyUri;
    }

    /// <summary>What the destination server received, or null while nothing has arrived.</summary>
    public RecordedRequest? Received => _state.Received;

    /// <summary>Everything the transform logged at Warning or above.</summary>
    public IReadOnlyList<string> Warnings => _state.Logger.WarningsAndAbove;

    /// <summary>Every telemetry event the forwarder published so far, in publish order.</summary>
    public IReadOnlyList<TelemetryEvent> Telemetry => [.. _state.Telemetry];

    public Uri DestinationUri { get; }

    public Uri ProxyUri { get; }

    /// <summary>
    /// Starts both listeners.
    ///
    /// The defaults are what the proxy ships: the real transformer, a mutation that changes
    /// nothing, and the configured rewrite limit. <paramref name="transformerFactory"/> is
    /// for the cases that need something other than the real transformer entirely.
    /// </summary>
    public static async Task<ForwardingHarness> StartAsync(
        IBodyMutationFactory? mutation = null,
        BodyLimits? limits = null,
        Func<Uri, HttpTransformer>? transformerFactory = null,
        Func<HttpContext, byte[], Task>? respond = null)
    {
        HarnessState state = new();
        ITelemetrySink sink = new QueueTelemetrySink(state.Telemetry);

        // The trace is per request, and the real transformer reports to it the way production
        // does; a test-supplied transformer gets the trace's completion only.
        Func<Uri, ExchangeTrace, HttpTransformer> transformers = transformerFactory is not null
            ? (target, _) => transformerFactory(target)
            : (target, trace) => new ForwardProxyTransformer(
                target,
                (mutation ?? new PassthroughMutationFactory()).CreateForExchange(
                    new ClientIdentity("harness"),
                    target,
                    trace),
                limits ?? new BodyLimits(BodyLimits.DefaultMaxMutableBodyBytes),
                // Unconfigured, so every path is inspected and these keep testing the forwarding
                // itself rather than the narrowing -- InspectionScopeTests covers that.
                new InspectionScope(new ConfigurationBuilder().Build(), NullLogger<InspectionScope>.Instance),
                state.Logger,
                trace);

        WebApplication destinationApp = BuildApp();
        destinationApp.Run(async context =>
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

            // The recorder has already drained the request, so the responder is handed what
            // it read rather than an empty stream.
            if (respond is not null)
                await respond(context, state.Received.Body);
            else
                await context.Response.WriteAsync("ok", context.RequestAborted);
        });

        await destinationApp.StartAsync();
        Uri destinationUri = new(destinationApp.Urls.First());

        WebApplication forwarderApp = BuildApp(services => services.AddHttpForwarder());

        // Kept short so a transform that never lets go fails the test rather than stalling
        // the suite. Production allows two minutes; nothing here needs more than a moment.
        ForwarderRequestConfig requestConfig = new() { ActivityTimeout = TimeSpan.FromSeconds(10) };

        // Mirrors ForwardProxy's own handler: no decompression, no cookies, no nested proxy,
        // so whatever reaches the destination is what the transform produced.
        HttpMessageInvoker upstream = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
        });

        IHttpForwarder httpForwarder = forwarderApp.Services.GetRequiredService<IHttpForwarder>();

        forwarderApp.Run(async context =>
        {
            Uri target = ForwardProxy.GetProxyDestination(context)
                         ?? new Uri(destinationUri, context.Request.Path + context.Request.QueryString);

            ExchangeTrace trace = new(
                sink,
                CorrelationIds.NextRequest(),
                new RequestFacts(
                    "127.0.0.1",
                    "Test · .1",
                    context.Request.Method,
                    TelemetryScheme.Http,
                    target.Host,
                    target.PathAndQuery,
                    context.Request.ContentType,
                    context.Request.ContentLength ?? 0));

            ForwarderError error = await httpForwarder.SendAsync(
                context,
                target.GetLeftPart(UriPartial.Authority),
                upstream,
                requestConfig,
                transformers(target, trace));

            trace.Completed(context.Response.StatusCode, 0, 0);

            await state.Completions.Writer.WriteAsync(error);
        });

        await forwarderApp.StartAsync();

        return new ForwardingHarness(
            destinationApp,
            forwarderApp,
            upstream,
            state,
            destinationUri,
            new Uri(forwarderApp.Urls.First()));
    }

    /// <summary>A client that treats the forwarder as its HTTP proxy, so request lines go out
    /// in absolute form.</summary>
    public HttpClient CreateProxiedClient() => new(new SocketsHttpHandler
    {
        Proxy = new WebProxy(ProxyUri),
        UseProxy = true,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// The <see cref="ForwarderError"/> of the next forwarded request to finish. Throws on
    /// timeout, which is how a transform that never lets go of the request is reported.
    /// </summary>
    public async Task<ForwarderError> NextCompletionAsync(TimeSpan timeout)
    {
        using CancellationTokenSource expiry = new(timeout);

        try
        {
            return await _state.Completions.Reader.ReadAsync(expiry.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"The forwarder did not finish within {timeout.TotalSeconds:0.#}s. The client's request body was most likely never drained.");
        }
    }

    /// <summary>The destination's request, or a failed assertion when nothing arrived.</summary>
    public RecordedRequest RequireReceived()
    {
        RecordedRequest? received = _state.Received;
        Assert.IsNotNull(received, "The destination server received no request at all.");

        return received;
    }

    public async ValueTask DisposeAsync()
    {
        await _forwarder.StopAsync();
        await _destination.StopAsync();
        _upstream.Dispose();
        await _forwarder.DisposeAsync();
        await _destination.DisposeAsync();
    }

    private static WebApplication BuildApp(Action<IServiceCollection>? configureServices = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        configureServices?.Invoke(builder.Services);

        return builder.Build();
    }
}
