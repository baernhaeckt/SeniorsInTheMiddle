using System.Net.Sockets;
using System.Text.Json;

namespace SeniorsInTheMiddle.Proxy.Services;

/// <summary>
/// One python service, reached over its own unix socket.
///
/// The connection is opened on first use and shared by every caller; the client
/// multiplexes calls by id. When the socket breaks -- supervisord restarted the daemon,
/// say -- the connection is dropped and the next call reconnects, so a service that
/// bounced never needs the proxy to be restarted.
/// </summary>
public sealed class ServiceConnection : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;
    private ServiceSocketClient? _client;

    public ServiceConnection(string name, ServiceEndpointOptions options, ILogger logger)
    {
        Name = name;
        Options = options;
        _logger = logger;
    }

    public string Name { get; }

    public ServiceEndpointOptions Options { get; }

    /// <summary>False when no socket path is set; every call then throws
    /// <see cref="ServiceUnavailableException"/>.</summary>
    public bool IsConfigured => Options.IsConfigured;

    /// <summary>Calls <paramref name="method"/> and returns the raw <c>result</c> element.
    /// The payload is serialized with camelCase; name properties in snake_case yourself
    /// where the python side expects it (<c>new { pii_type = x }</c> stays <c>pii_type</c>).
    ///
    /// A call that takes longer than <see cref="ServiceEndpointOptions.CallTimeoutSeconds"/>
    /// throws <see cref="ServiceUnavailableException"/>. The connection is kept: the daemon
    /// is slow, not gone, and the late answer is dropped by the client when it arrives.</summary>
    public async Task<JsonElement> CallAsync(string method, object? payload = null, CancellationToken cancellationToken = default)
    {
        ServiceSocketClient connected = await GetClientAsync(cancellationToken);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Options.CallTimeoutSeconds));

        try
        {
            return await connected.CallAsync(method, payload, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServiceUnavailableException(
                Name,
                $"The {Name} service did not answer {method} within {Options.CallTimeoutSeconds}s.",
                ex);
        }
        // JsonException and KeyNotFoundException belong here too: a malformed or incomplete
        // response frame kills the client's read loop, and a connection whose read loop is
        // gone is as unusable as one whose socket is.
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
            or InvalidDataException or JsonException or KeyNotFoundException)
        {
            await DropAsync(connected, ex);
            throw new ServiceUnavailableException(Name, $"The {Name} service connection failed.", ex);
        }
    }

    /// <summary>Round trip through the runtime's built-in <c>$ping</c>.</summary>
    public async Task PingAsync(CancellationToken cancellationToken = default)
        => await CallAsync("$ping", null, cancellationToken);

    /// <summary>The runtime's built-in <c>$info</c>: service name, protocol, version.</summary>
    public Task<JsonElement> InfoAsync(CancellationToken cancellationToken = default)
        => CallAsync("$info", null, cancellationToken);

    private async Task<ServiceSocketClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new ServiceUnavailableException(Name, $"The {Name} service has no socket path configured (Services:{Name}:SocketPath).");

        // Read outside the gate, so a connected caller never waits on one that is connecting.
        ServiceSocketClient? existing = Volatile.Read(ref _client);
        if (existing is not null)
            return existing;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
                return _client;

            try
            {
                _client = await ServiceSocketClient.ConnectAsync(
                    Options.SocketPath,
                    TimeSpan.FromSeconds(Options.ConnectTimeoutSeconds),
                    Options.MaxFrameBytes,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is TimeoutException or SocketException or IOException)
            {
                throw new ServiceUnavailableException(Name, $"Could not connect to the {Name} service at {Options.SocketPath}.", ex);
            }

            _logger.LogInformation("Connected to the {Service} service at {SocketPath}", Name, Options.SocketPath);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DropAsync(ServiceSocketClient failed, Exception reason)
    {
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_client, failed))
                return;

            _client = null;
            _logger.LogWarning(reason, "Lost the connection to the {Service} service; reconnecting on the next call", Name);
        }
        finally
        {
            _gate.Release();
        }

        await failed.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        ServiceSocketClient? open = Interlocked.Exchange(ref _client, null);
        if (open is not null)
            await open.DisposeAsync();

        _gate.Dispose();
    }
}

/// <summary>The service is disabled, unreachable, or the connection broke mid-call.</summary>
public sealed class ServiceUnavailableException(string service, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Service { get; } = service;
}
