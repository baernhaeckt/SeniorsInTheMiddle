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
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ILogger logger;
    private ServiceSocketClient? client;

    public ServiceConnection(string name, ServiceEndpointOptions options, ILogger logger)
    {
        Name = name;
        Options = options;
        this.logger = logger;
    }

    public string Name { get; }

    public ServiceEndpointOptions Options { get; }

    /// <summary>False when no socket path is set; every call then throws
    /// <see cref="ServiceUnavailableException"/>.</summary>
    public bool IsConfigured => Options.IsConfigured;

    /// <summary>True while a connection is open. Does not probe the socket.</summary>
    public bool IsConnected => client is not null;

    /// <summary>Calls <paramref name="method"/> and returns the raw <c>result</c> element.
    /// The payload is serialized with camelCase; name properties in snake_case yourself
    /// where the python side expects it (<c>new { pii_type = x }</c> stays <c>pii_type</c>).</summary>
    public async Task<JsonElement> CallAsync(string method, object? payload = null, CancellationToken cancellationToken = default)
    {
        ServiceSocketClient connected = await GetClientAsync(cancellationToken);
        try
        {
            return await connected.CallAsync(method, payload, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException)
        {
            await DropAsync(connected, ex);
            throw new ServiceUnavailableException(Name, $"The {Name} service connection failed.", ex);
        }
    }

    /// <summary>Calls <paramref name="method"/> and deserializes the result with
    /// <paramref name="serializerOptions"/>.</summary>
    public async Task<T?> CallAsync<T>(
        string method,
        object? payload,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken = default)
    {
        JsonElement result = await CallAsync(method, payload, cancellationToken);
        return result.ValueKind == JsonValueKind.Undefined ? default : result.Deserialize<T>(serializerOptions);
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

        ServiceSocketClient? existing = client;
        if (existing is not null)
            return existing;

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (client is not null)
                return client;

            try
            {
                client = await ServiceSocketClient.ConnectAsync(
                    Options.SocketPath,
                    TimeSpan.FromSeconds(Options.ConnectTimeoutSeconds),
                    Options.MaxFrameBytes,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is TimeoutException or SocketException or IOException)
            {
                throw new ServiceUnavailableException(Name, $"Could not connect to the {Name} service at {Options.SocketPath}.", ex);
            }

            logger.LogInformation("Connected to the {Service} service at {SocketPath}", Name, Options.SocketPath);
            return client;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DropAsync(ServiceSocketClient failed, Exception reason)
    {
        await gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(client, failed))
                return;

            client = null;
            logger.LogWarning(reason, "Lost the connection to the {Service} service; reconnecting on the next call", Name);
        }
        finally
        {
            gate.Release();
        }

        await failed.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        ServiceSocketClient? open = Interlocked.Exchange(ref client, null);
        if (open is not null)
            await open.DisposeAsync();

        gate.Dispose();
    }
}

/// <summary>The service is disabled, unreachable, or the connection broke mid-call.</summary>
public sealed class ServiceUnavailableException(string service, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Service { get; } = service;
}
