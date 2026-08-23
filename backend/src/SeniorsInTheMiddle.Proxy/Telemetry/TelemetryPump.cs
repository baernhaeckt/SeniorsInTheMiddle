using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;

namespace SeniorsInTheMiddle.Proxy.Telemetry;

/// <summary>
/// The one hop between the proxy and the hub.
///
/// Publishing is a non-blocking write to a bounded queue; a single reader drains it and
/// awaits each send, so frames reach every dashboard in the order they happened. A slow
/// dashboard therefore stalls the queue rather than the proxy, and once the queue is full
/// new events are dropped and counted instead of blocking a request.
///
/// The queue drops the newest rather than the oldest, because the protocol promises that
/// a request.observed is followed by a request.completed. Dropping the oldest would leave
/// the dashboard with completions for rows it never received.
/// </summary>
sealed class TelemetryPump : BackgroundService, ITelemetrySink
{
    private readonly Channel<TelemetryEvent> _queue;
    private readonly IHubContext<TelemetryHub> _hub;
    private readonly ILogger<TelemetryPump> _logger;

    private long _dropped;
    private long _reportedDrops;

    public TelemetryPump(
        IHubContext<TelemetryHub> hub,
        IConfiguration configuration,
        ILogger<TelemetryPump> logger)
    {
        _hub = hub;
        _logger = logger;

        int capacity = Math.Max(16, configuration.GetValue("Telemetry:QueueCapacity", 2048));

        // The drop is counted through this callback rather than from TryWrite's result:
        // under DropWrite the write is reported as having succeeded even when the item was
        // discarded, so a caller cannot tell a queued event from a dropped one.
        _queue = Channel.CreateBounded<TelemetryEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    public void Publish(TelemetryEvent telemetryEvent) => _queue.Writer.TryWrite(telemetryEvent);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (TelemetryEvent telemetryEvent in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await SendAsync(telemetryEvent, stoppingToken);
                await ReportDropsAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task SendAsync(TelemetryEvent telemetryEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _hub.Clients.All.SendAsync("event", TelemetryJson.Serialize(telemetryEvent), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A dashboard that went away mid-send must not take the pump with it.
            _logger.LogDebug(exception, "Dropping a telemetry frame the hub would not take.");
        }
    }

    /// <summary>
    /// Losing frames silently would make the dashboard look merely quiet, so the first
    /// send after a drop carries the count into the ticker.
    /// </summary>
    private async Task ReportDropsAsync(CancellationToken cancellationToken)
    {
        long total = Interlocked.Read(ref _dropped);
        if (total == _reportedDrops)
            return;

        long missed = total - _reportedDrops;
        _reportedDrops = total;
        _logger.LogWarning("Telemetry queue full; dropped {Count} events.", missed);

        await SendAsync(
            new ProxyLog(
                TelemetryJson.Now(),
                TelemetryLogLevel.Warn,
                $"Telemetry queue full — {missed} events dropped."),
            cancellationToken);
    }
}
