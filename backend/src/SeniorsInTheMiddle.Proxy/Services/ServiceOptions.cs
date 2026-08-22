namespace SeniorsInTheMiddle.Proxy.Services;

/// <summary>
/// The python services this process talks to, one unix socket each.
///
/// Bound from the <c>Services</c> section: <c>Services:Pii:SocketPath</c>, or as an
/// environment variable <c>Services__Pii__SocketPath</c>. A service whose socket path is
/// empty is disabled -- the normal state on a Windows dev box, where there is no container
/// and no unix socket. The image sets the paths in backend/Dockerfile, in step with the
/// supervisord programs that open them.
/// </summary>
public sealed class ServiceOptions
{
    public const string SectionName = "Services";

    /// <summary>Keyed by service name, e.g. <c>Pii</c>. Keys are case-insensitive.</summary>
    public Dictionary<string, ServiceEndpointOptions> Endpoints { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static ServiceOptions From(IConfiguration configuration)
    {
        ServiceOptions options = new();
        foreach (IConfigurationSection section in configuration.GetSection(SectionName).GetChildren())
        {
            ServiceEndpointOptions endpoint = new();
            section.Bind(endpoint);
            options.Endpoints[section.Key] = endpoint;
        }

        return options;
    }

    public ServiceEndpointOptions Get(string name)
        => Endpoints.TryGetValue(name, out ServiceEndpointOptions? endpoint) ? endpoint : new ServiceEndpointOptions();
}

public sealed class ServiceEndpointOptions
{
    /// <summary>Absolute path of the service's unix socket. Empty disables the service.</summary>
    public string SocketPath { get; set; } = string.Empty;

    /// <summary>How long a connect attempt keeps retrying before giving up. The daemon may
    /// still be loading its model when the proxy comes up.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 30;

    /// <summary>Largest frame accepted from the service; matches SERVICE_MAX_FRAME_BYTES.</summary>
    public int MaxFrameBytes { get; set; } = 8 * 1024 * 1024;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SocketPath);
}
