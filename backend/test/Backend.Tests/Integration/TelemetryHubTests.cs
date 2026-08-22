using System.Net;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using SeniorsInTheMiddle.Proxy.Telemetry;

namespace Backend.Tests.Integration;

[TestClass]
public class TelemetryHubTests
{
    private WebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public void Setup() => _factory = new CustomWebApplicationFactory<Program>();

    [TestCleanup]
    public void Cleanup() => _factory?.Dispose();

    [TestMethod]
    public async Task Connecting_DeliversHelloFirst()
    {
        List<string> frames = [];
        await using HubConnection connection = await ConnectAsync(frames);

        await connection.StartAsync(TestContext.CancellationTokenSource.Token);
        string hello = await WaitForFrameAsync(frames, 0);

        StringAssert.Contains(hello, "\"type\":\"hello\"");
        StringAssert.Contains(hello, "\"version\":3");
        StringAssert.Contains(hello, "\"policy\":{\"rewrite\":true");
        StringAssert.Contains(hello, "\"services\":{\"pii\":\"disabled\",\"privacyCheck\":\"disabled\"}");
    }

    [TestMethod]
    public async Task PublishedEvents_ReachTheDashboard()
    {
        List<string> frames = [];
        await using HubConnection connection = await ConnectAsync(frames);
        await connection.StartAsync(TestContext.CancellationTokenSource.Token);
        await WaitForFrameAsync(frames, 0);

        ITelemetrySink sink = _factory.Services.GetRequiredService<ITelemetrySink>();
        sink.Publish(new RequestObserved(
            "r-00001", 1, "127.0.0.1", "Device · .1", "GET", TelemetryScheme.Http,
            "receiver", "/assets/app.css", "text/css", 0, Treatment.Passthrough, "text/css"));

        string frame = await WaitForFrameAsync(frames, 1);

        StringAssert.Contains(frame, "\"type\":\"request.observed\"");
        StringAssert.Contains(frame, "\"requestId\":\"r-00001\"");
        StringAssert.Contains(frame, "\"treatment\":\"passthrough\"");
    }

    [TestMethod]
    public async Task AnUnauthenticatedConnectionIsRefused()
    {
        // The regression test for the hole this stream used to have: the origin guard lets
        // anything without an Origin header through, so before the hub required a user, any
        // non-browser caller could read decrypted traffic off it.
        List<string> frames = [];
        await using HubConnection connection = Build(frames, token: null);

        // The transport decides what it throws when an upgrade is refused, so this asserts
        // that the connection failed rather than pinning an exception type.
        Exception? caught = null;
        try
        {
            await connection.StartAsync(TestContext.CancellationTokenSource.Token);
        }
        catch (Exception exception)
        {
            caught = exception;
        }

        Assert.IsNotNull(caught, "An unauthenticated caller reached the telemetry stream.");
        Assert.AreNotEqual(HubConnectionState.Connected, connection.State);
    }

    [TestMethod]
    public async Task ATokenInTheQueryStringIsAccepted()
    {
        // How a browser actually authenticates here. It cannot put a header on a WebSocket
        // handshake, so the SignalR client appends the token to the URL and the server picks
        // it up in OnMessageReceived. Negotiate sits under the same path and the same hook.
        HttpClient client = _factory.CreateClient();
        string token = await TestAuth.TokenAsync(client, TestContext.CancellationTokenSource.Token);

        HttpResponseMessage response = await client.PostAsync(
            $"/hub/telemetry/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}",
            content: null,
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AGarbageTokenInTheQueryStringIsRefused()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            "/hub/telemetry/negotiate?negotiateVersion=1&access_token=not-a-jwt",
            content: null,
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task AForeignOriginIsRefused()
    {
        // A WebSocket handshake never reaches CORS, so this stops a page a signed-in viewer
        // happens to visit from opening the stream with their session.
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "https://not-the-dashboard.example");

        HttpResponseMessage response = await client.GetAsync(
            "/hub/telemetry/negotiate?negotiateVersion=1",
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task AForeignOriginIsRefused_EvenWithAValidToken()
    {
        // The threat the origin guard actually exists for. A signed-in viewer visits some
        // other page; that page opens the hub and the browser attaches their session to it.
        // Authentication cannot catch this, because the token is genuine.
        HttpClient client = _factory.CreateClient();
        string token = await TestAuth.TokenAsync(client, TestContext.CancellationTokenSource.Token);
        client.DefaultRequestHeaders.Add("Origin", "https://not-the-dashboard.example");

        HttpResponseMessage response = await client.PostAsync(
            $"/hub/telemetry/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}",
            content: null,
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task AConfiguredOriginIsLetThrough()
    {
        // CustomWebApplicationFactory runs in Development, whose Cors:AllowedOrigins lists
        // the Vite dev server. Origin is the browser check; the token is the other one, so
        // this needs both to reach 200.
        HttpClient client = _factory.CreateClient();
        string token = await TestAuth.TokenAsync(client, TestContext.CancellationTokenSource.Token);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");

        HttpResponseMessage response = await client.PostAsync(
            $"/hub/telemetry/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}",
            content: null,
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HubConnection> ConnectAsync(List<string> frames)
    {
        string token = await TestAuth.TokenAsync(
            _factory.CreateClient(),
            TestContext.CancellationTokenSource.Token);

        return Build(frames, token);
    }

    private HubConnection Build(List<string> frames, string? token)
    {
        TestServer server = _factory.Server;

        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, TelemetryRoutes.HubPath), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    WebSocketClient client = server.CreateWebSocketClient();

                    // TestServer's WebSocket client ignores the ClientWebSocketOptions SignalR
                    // would normally have put the token on, so it goes on by hand. A real
                    // browser cannot set this header at all and uses the query string instead
                    // — covered by ATokenInTheQueryStringIsAccepted.
                    if (token is not null)
                    {
                        client.ConfigureRequest = request =>
                            request.Headers["Authorization"] = $"Bearer {token}";
                    }

                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };

                if (token is not null)
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        connection.On<string>("event", frame =>
        {
            lock (frames)
            {
                frames.Add(frame);
            }
        });

        return connection;
    }

    private static async Task<string> WaitForFrameAsync(List<string> frames, int index)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            lock (frames)
            {
                if (frames.Count > index)
                    return frames[index];
            }

            await Task.Delay(50);
        }

        Assert.Fail($"No telemetry frame at index {index} after 5 seconds.");
        return string.Empty;
    }

    public TestContext TestContext { get; set; } = null!;
}
