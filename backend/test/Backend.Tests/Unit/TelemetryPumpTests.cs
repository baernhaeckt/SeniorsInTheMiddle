using System.Text.Json;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Telemetry;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins what the queue between the proxy and the dashboard does when it cannot keep up.
///
/// The pump is on the request path in one direction only: <see cref="ITelemetrySink.Publish"/>
/// is called from request and tunnel threads and must never block one, and a dashboard that
/// stopped reading must stall the queue rather than the proxy. What it drops when that happens
/// is a protocol matter, not a capacity one -- the dashboard is promised that a
/// request.observed precedes its request.completed, so the oldest have to survive.
/// </summary>
[TestClass]
public class TelemetryPumpTests
{
    /// <summary>The smallest capacity the pump accepts, so a queue can be filled quickly.</summary>
    private const int Capacity = 16;

    /// <summary>How long a frame may take to arrive before the test calls it a hang.</summary>
    private static readonly TimeSpan FrameBound = TimeSpan.FromSeconds(30);

    private static TelemetryPump Pump(RecordingHub hub)
        => new(
            hub,
            new ConfigurationBuilder()
                .AddInMemoryCollection([new KeyValuePair<string, string?>("Telemetry:QueueCapacity", Capacity.ToString())])
                .Build(),
            NullLogger<TelemetryPump>.Instance);

    private static ProxyLog Log(string message)
        => new(TelemetryJson.Now(), TelemetryLogLevel.Info, message);

    [TestMethod]
    public async Task Published_Events_Reach_The_Hub_In_Order()
    {
        RecordingHub hub = new();
        TelemetryPump pump = Pump(hub);
        await pump.StartAsync(CancellationToken.None);

        try
        {
            for (int index = 0; index < 5; index++)
                pump.Publish(Log($"event-{index}"));

            await hub.WaitForAsync(5, FrameBound);

            CollectionAssert.AreEqual(
                Enumerable.Range(0, 5).Select(i => $"event-{i}").ToArray(),
                hub.Messages.ToArray());
        }
        finally
        {
            await pump.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Publishing is a non-blocking write, so a dashboard that stopped reading costs a
    /// proxied request nothing. If this ever regresses it is not a slow dashboard, it is a
    /// slow proxy.
    /// </summary>
    [TestMethod]
    public async Task Publishing_Does_Not_Block_When_The_Hub_Has_Stalled()
    {
        RecordingHub hub = new();
        TelemetryPump pump = Pump(hub);
        hub.Stall();
        await pump.StartAsync(CancellationToken.None);

        try
        {
            pump.Publish(Log("first"));
            await hub.WaitUntilStalledAsync(FrameBound);

            // Far more than the queue holds, from the thread a request would be on.
            Task publishing = Task.Run(() =>
            {
                for (int index = 0; index < Capacity * 10; index++)
                    pump.Publish(Log($"event-{index}"));
            });

            await publishing.WaitAsync(FrameBound);
        }
        finally
        {
            hub.Release();
            await pump.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The protocol promise: a request.observed is followed by a request.completed. Dropping
    /// the oldest would leave the dashboard with completions for rows it never received, so
    /// the newest go instead.
    /// </summary>
    [TestMethod]
    public async Task A_Full_Queue_Drops_The_Newest_And_Keeps_The_Oldest()
    {
        RecordingHub hub = new();
        TelemetryPump pump = Pump(hub);
        hub.Stall();
        await pump.StartAsync(CancellationToken.None);

        try
        {
            // Taken by the reader, which then blocks in the hub; the queue behind it is empty.
            pump.Publish(Log("event-0"));
            await hub.WaitUntilStalledAsync(FrameBound);

            // Fills the queue exactly, then overruns it.
            for (int index = 1; index <= Capacity + 5; index++)
                pump.Publish(Log($"event-{index}"));

            hub.Release();

            // The last event that fits. Waiting on it rather than on a frame count keeps the
            // assertion independent of the drop report the pump interleaves; the queue is
            // first-in-first-out, so anything still to come would have come before it.
            await hub.WaitForMatchAsync(m => m == $"event-{Capacity}", FrameBound);

            string[] delivered = [.. hub.Messages.Where(m => m.StartsWith("event-", StringComparison.Ordinal))];

            CollectionAssert.AreEqual(
                Enumerable.Range(0, Capacity + 1).Select(i => $"event-{i}").ToArray(),
                delivered,
                "The queue kept the wrong end of the burst.");
        }
        finally
        {
            hub.Release();
            await pump.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Losing frames silently would make the dashboard look merely quiet rather than
    /// incomplete, and someone reading it would conclude the proxy saw less traffic than it
    /// did. The count has to reach the ticker.
    /// </summary>
    [TestMethod]
    public async Task Dropped_Events_Are_Reported_To_The_Dashboard()
    {
        RecordingHub hub = new();
        TelemetryPump pump = Pump(hub);
        hub.Stall();
        await pump.StartAsync(CancellationToken.None);

        try
        {
            pump.Publish(Log("event-0"));
            await hub.WaitUntilStalledAsync(FrameBound);

            for (int index = 1; index <= Capacity + 5; index++)
                pump.Publish(Log($"event-{index}"));

            hub.Release();

            await hub.WaitForMatchAsync(m => m.Contains("events dropped", StringComparison.Ordinal), FrameBound);

            string report = hub.Messages.Single(m => m.Contains("events dropped", StringComparison.Ordinal));
            Assert.Contains("5 events dropped", report);
        }
        finally
        {
            hub.Release();
            await pump.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The report is made once per burst, not once per frame that follows it.</summary>
    [TestMethod]
    public async Task The_Drop_Report_Is_Not_Repeated()
    {
        RecordingHub hub = new();
        TelemetryPump pump = Pump(hub);
        hub.Stall();
        await pump.StartAsync(CancellationToken.None);

        try
        {
            pump.Publish(Log("event-0"));
            await hub.WaitUntilStalledAsync(FrameBound);

            for (int index = 1; index <= Capacity + 5; index++)
                pump.Publish(Log($"event-{index}"));

            hub.Release();
            await hub.WaitForMatchAsync(m => m == $"event-{Capacity}", FrameBound);

            pump.Publish(Log("after"));
            await hub.WaitForMatchAsync(m => m == "after", FrameBound);

            Assert.HasCount(
                1,
                hub.Messages.Where(m => m.Contains("events dropped", StringComparison.Ordinal)).ToArray());
        }
        finally
        {
            hub.Release();
            await pump.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A dashboard that went away mid-send throws from the hub. The pump has to keep going:
    /// one browser tab closing must not end telemetry for every other viewer.
    /// </summary>
    [TestMethod]
    public async Task A_Hub_That_Throws_Does_Not_Stop_The_Pump()
    {
        RecordingHub hub = new();
        TelemetryPump pump = Pump(hub);
        hub.FailNextSend();
        await pump.StartAsync(CancellationToken.None);

        try
        {
            pump.Publish(Log("lost"));
            pump.Publish(Log("delivered"));

            await hub.WaitForMatchAsync(m => m == "delivered", FrameBound);
        }
        finally
        {
            await pump.StopAsync(CancellationToken.None);
        }
    }
}

/// <summary>
/// A hub context that records the frames sent to it and can be made to stall or fail on
/// demand, so the pump's behaviour under a dashboard that stopped reading is observable
/// without a browser.
/// </summary>
internal sealed class RecordingHub : IHubContext<TelemetryHub>, IClientProxy
{
    private readonly List<string> _messages = [];
    private readonly SemaphoreSlim _arrived = new(0);

    private TaskCompletionSource _gate = Completed();
    private TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _failNext;

    public IHubClients Clients => new RecordingClients(this);

    public IGroupManager Groups => throw new NotSupportedException("The telemetry hub uses no groups.");

    /// <summary>The frames delivered so far, oldest first.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
                return [.. _messages];
        }
    }

    /// <summary>Blocks every send from now on, the way a dashboard that stopped reading does.</summary>
    public void Stall()
    {
        _stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Lets the stalled sends through.</summary>
    public void Release() => _gate.TrySetResult();

    /// <summary>Makes the next send throw, the way a connection that went away mid-send does.</summary>
    public void FailNextSend() => Interlocked.Exchange(ref _failNext, 1);

    /// <summary>Waits until a send is actually blocked, so a burst published after this is
    /// known to queue up behind it rather than racing the reader.</summary>
    public Task WaitUntilStalledAsync(TimeSpan bound) => _stalled.Task.WaitAsync(bound);

    /// <summary>Waits for <paramref name="count"/> frames, and fails rather than returning
    /// early if they do not arrive: a silent timeout would turn every assertion after it into
    /// one that passes for the wrong reason.</summary>
    public async Task WaitForAsync(int count, TimeSpan bound)
    {
        for (int seen = 0; seen < count; seen++)
        {
            if (!await _arrived.WaitAsync(bound))
                throw new TimeoutException($"Only {seen} of {count} frames arrived within {bound}.");
        }
    }

    public async Task WaitForMatchAsync(Func<string, bool> predicate, TimeSpan bound)
    {
        while (!Messages.Any(predicate))
        {
            if (!await _arrived.WaitAsync(bound))
                throw new TimeoutException($"No matching frame arrived within {bound}.");
        }
    }

    public async Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        _stalled.TrySetResult();
        await _gate.Task.WaitAsync(cancellationToken);

        if (Interlocked.Exchange(ref _failNext, 0) == 1)
            throw new IOException("The dashboard went away mid-send.");

        // The pump serializes to a JSON string; the message is the only part the tests read.
        string frame = args[0] as string ?? string.Empty;
        lock (_messages)
            _messages.Add(MessageOf(frame));

        _arrived.Release();
    }

    /// <summary>
    /// Pulls the message back out of a serialized log frame.
    ///
    /// Input:  {"type":"log","ts":"...","level":"info","message":"event-3"}
    /// Output: "event-3"
    /// </summary>
    private static string MessageOf(string frame)
    {
        using JsonDocument document = JsonDocument.Parse(frame);
        return document.RootElement.TryGetProperty("message", out JsonElement message)
            ? message.GetString() ?? string.Empty
            : frame;
    }

    private static TaskCompletionSource Completed()
    {
        TaskCompletionSource source = new();
        source.SetResult();
        return source;
    }
}

/// <summary>
/// The client set behind <see cref="RecordingHub"/>. The pump only ever sends to
/// <see cref="All"/>; every other way of addressing clients throws rather than answering, so
/// a pump that started addressing individuals fails here instead of going unasserted.
/// </summary>
internal sealed class RecordingClients(RecordingHub hub) : IHubClients
{
    public IClientProxy All => hub;

    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

    public ISingleClientProxy Client(string connectionId) => throw new NotSupportedException();

    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

    public IClientProxy Group(string groupName) => throw new NotSupportedException();

    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

    public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

    public IClientProxy User(string userId) => throw new NotSupportedException();

    public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();

    IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => throw new NotSupportedException();
}
