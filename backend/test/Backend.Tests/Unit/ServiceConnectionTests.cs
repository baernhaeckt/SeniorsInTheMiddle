using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Services;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins what happens to the proxy when a python daemon is slow, restarted, or was never
/// there at all.
///
/// The proxy and the services share a container but not a lifetime: supervisord restarts a
/// daemon that crashed, and the proxy keeps running with a socket that is now dead. Every
/// test here is about that seam, because getting it wrong is not a failed request but a
/// proxy that has to be restarted by hand to start detecting again.
/// </summary>
[TestClass]
public class ServiceConnectionTests
{
    /// <summary>How long a call may take before the test calls it a hang.</summary>
    private static readonly TimeSpan CallBound = TimeSpan.FromSeconds(30);

    private static ServiceConnection Connect(string socketPath, int callTimeoutSeconds = 120)
        => new(
            "Pii",
            new ServiceEndpointOptions
            {
                SocketPath = socketPath,
                ConnectTimeoutSeconds = 2,
                CallTimeoutSeconds = callTimeoutSeconds,
            },
            NullLogger.Instance);

    [TestMethod]
    public async Task A_Call_Returns_The_Services_Result()
    {
        await using StubPythonService service = StubPythonService.Start();
        await using ServiceConnection connection = Connect(service.SocketPath);

        JsonElement result = await connection.CallAsync("$ping").WaitAsync(CallBound);

        Assert.IsTrue(result.GetProperty("pong").GetBoolean());
    }

    /// <summary>
    /// The reason this class exists. supervisord restarting the daemon breaks the socket but
    /// not the path; without the reconnect every later call would fail for the life of the
    /// process, and the proxy would keep forwarding with detection silently switched off.
    /// </summary>
    [TestMethod]
    public async Task A_Broken_Connection_Is_Reopened_On_The_Next_Call()
    {
        await using StubPythonService service = StubPythonService.Start();
        await using ServiceConnection connection = Connect(service.SocketPath);

        await connection.PingAsync().WaitAsync(CallBound);
        Assert.AreEqual(1, service.ConnectionCount);

        service.DropConnections();

        // The call that discovers the break is the one that fails. That is the contract:
        // a lost connection is reported, not retried behind the caller's back, because a
        // request whose body already went out must not be replayed.
        await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(
            () => connection.PingAsync().WaitAsync(CallBound));

        await connection.PingAsync().WaitAsync(CallBound);
        Assert.AreEqual(2, service.ConnectionCount, "The connection was not reopened.");
    }

    /// <summary>
    /// One connection is shared by every caller -- the protocol multiplexes by id -- so
    /// concurrent first calls must not each open one. Opening several would leak sockets
    /// under exactly the load that makes them expensive.
    /// </summary>
    [TestMethod]
    public async Task Concurrent_First_Calls_Open_A_Single_Connection()
    {
        await using StubPythonService service = StubPythonService.Start();
        await using ServiceConnection connection = Connect(service.SocketPath);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => connection.PingAsync()))
            .WaitAsync(CallBound);

        Assert.AreEqual(1, service.ConnectionCount);
    }

    /// <summary>
    /// A daemon that is slow is not a daemon that is gone. The call gives up, but the
    /// connection stays: dropping it would make every subsequent call pay a reconnect for a
    /// service that is merely busy.
    /// </summary>
    [TestMethod]
    public async Task A_Call_That_Outlives_Its_Timeout_Fails_But_Keeps_The_Connection()
    {
        await using StubPythonService service = StubPythonService.StartSlow(TimeSpan.FromSeconds(3));
        await using ServiceConnection connection = Connect(service.SocketPath, callTimeoutSeconds: 1);

        ServiceUnavailableException failure = await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(
            () => connection.PingAsync().WaitAsync(CallBound));

        Assert.AreEqual("Pii", failure.Service);
        Assert.Contains("within 1s", failure.Message);

        // Same connection, so the service never saw a second one.
        await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(
            () => connection.PingAsync().WaitAsync(CallBound));

        Assert.AreEqual(1, service.ConnectionCount);
    }

    /// <summary>
    /// The caller's own cancellation is theirs. Reporting it as an unavailable service would
    /// blame the daemon for a client that hung up.
    /// </summary>
    [TestMethod]
    public async Task The_Callers_Cancellation_Is_Not_Reported_As_An_Unavailable_Service()
    {
        await using StubPythonService service = StubPythonService.StartSlow(TimeSpan.FromMinutes(5));
        await using ServiceConnection connection = Connect(service.SocketPath);

        using CancellationTokenSource cancelled = new();
        Task call = connection.PingAsync(cancelled.Token);
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => call.WaitAsync(CallBound));
    }

    /// <summary>
    /// The Windows dev box. The message names the configuration key, because the alternative
    /// is someone reading "unavailable" and going to look at a daemon that was never meant to
    /// be running.
    /// </summary>
    [TestMethod]
    public async Task An_Unconfigured_Service_Names_Its_Setting()
    {
        await using ServiceConnection connection = Connect(string.Empty);

        Assert.IsFalse(connection.IsConfigured);

        ServiceUnavailableException failure = await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(
            () => connection.PingAsync().WaitAsync(CallBound));

        Assert.Contains("Services:Pii:SocketPath", failure.Message);
    }

    /// <summary>A path nothing listens on fails within the connect timeout rather than
    /// retrying until the caller's own deadline.</summary>
    [TestMethod]
    public async Task A_Socket_Nothing_Listens_On_Fails_To_Connect()
    {
        await using ServiceConnection connection = Connect(FakeService.ShortSocketPath());

        ServiceUnavailableException failure = await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(
            () => connection.PingAsync().WaitAsync(CallBound));

        Assert.Contains("Could not connect", failure.Message);
    }

    /// <summary>Disposing twice is what a host shutdown after a failed start looks like.</summary>
    [TestMethod]
    public async Task Disposing_Twice_Is_Harmless()
    {
        await using StubPythonService service = StubPythonService.Start();
        ServiceConnection connection = Connect(service.SocketPath);

        await connection.PingAsync().WaitAsync(CallBound);

        await connection.DisposeAsync();
        await connection.DisposeAsync();
    }
}
