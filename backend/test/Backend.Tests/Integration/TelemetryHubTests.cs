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
        await using HubConnection connection = Connect(frames);

        await connection.StartAsync(TestContext.CancellationTokenSource.Token);
        string hello = await WaitForFrameAsync(frames, 0);

        StringAssert.Contains(hello, "\"type\":\"hello\"");
        StringAssert.Contains(hello, "\"version\":2");
    }

    [TestMethod]
    public async Task PublishedEvents_ReachTheDashboard()
    {
        List<string> frames = [];
        await using HubConnection connection = Connect(frames);
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
    public async Task AForeignOriginIsRefused()
    {
        // A WebSocket handshake never reaches CORS, so this is the only thing standing
        // between the stream and any page the viewer happens to have open.
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "https://not-the-dashboard.example");

        HttpResponseMessage response = await client.GetAsync(
            "/hub/telemetry/negotiate?negotiateVersion=1",
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task AConfiguredOriginIsLetThrough()
    {
        // CustomWebApplicationFactory runs in Development, whose Cors:AllowedOrigins lists
        // the Vite dev server.
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");

        HttpResponseMessage response = await client.PostAsync(
            "/hub/telemetry/negotiate?negotiateVersion=1",
            content: null,
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private HubConnection Connect(List<string> frames)
    {
        TestServer server = _factory.Server;

        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, TelemetryRoutes.HubPath), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                    await server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken);
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
