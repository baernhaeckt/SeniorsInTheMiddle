namespace SeniorsInTheMiddle.Proxy.Services;

/// <summary>
/// Every service known to this process, configured or not. Typed clients pick theirs by
/// name; the health check and the startup probe walk all of them.
/// </summary>
public sealed class ServiceConnections : IAsyncDisposable
{
    /// <summary>Services this build knows how to talk to. A name missing from the
    /// configuration is still listed, as disabled, so health output names it.</summary>
    public static readonly string[] KnownServices = [PiiService, PrivacyCheckService];

    public const string PiiService = "Pii";

    public const string PrivacyCheckService = "PrivacyCheck";

    private readonly Dictionary<string, ServiceConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    public ServiceConnections(ServiceOptions options, ILoggerFactory loggers)
    {
        foreach (string name in KnownServices.Concat(options.Endpoints.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _connections[name] = new ServiceConnection(
                name,
                options.Get(name),
                loggers.CreateLogger($"SeniorsInTheMiddle.Proxy.Services.{name}"));
        }
    }

    public IReadOnlyCollection<ServiceConnection> All => _connections.Values;

    public ServiceConnection Get(string name)
        => _connections.TryGetValue(name, out ServiceConnection? connection)
            ? connection
            : throw new KeyNotFoundException($"No service named '{name}' is registered.");

    public async ValueTask DisposeAsync()
    {
        foreach (ServiceConnection connection in _connections.Values)
            await connection.DisposeAsync();
    }
}
