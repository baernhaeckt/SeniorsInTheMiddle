namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Which paths on a host are worth rewriting, for hosts where the answer is "hardly any of them".
///
/// A site is not one thing. On chatgpt.com the prompt a user types goes to a handful of endpoints
/// under /backend-api/; everything else on that origin is the application itself -- scripts, fonts,
/// telemetry beacons, feature flags, session polling, and the bot challenge under /cdn-cgi/. None
/// of it contains anything a person typed, all of it is scanned at a cost, and each body scanned
/// is a body that can come back subtly wrong. A stand-in spliced into a settings JSON is a broken
/// preference; spliced into a challenge answer it is a page that never loads.
///
/// So a host listed here inverts the default. Instead of inspecting everything textual and
/// excluding what is known to break, it inspects nothing except the paths named -- which is the
/// safer direction to be wrong in, because the failure is "a body went unscanned" rather than "a
/// body was corrupted", and because the list of endpoints that carry a prompt is short and knowable
/// while the list of everything that must not be touched is neither.
///
/// This is not <see cref="InterceptionBypass"/> and does not overlap with it. Traffic here is still
/// decrypted, still traced, still visible in the dashboard; the only question is whether a body is
/// offered to the mutation. A bypassed host is not decrypted at all, and nothing about it can be
/// path-selective -- a CONNECT carries no path.
/// </summary>
sealed class InspectionScope
{
    private readonly List<(HostPattern Host, string[] Paths)> scoped = [];
    private readonly Dictionary<string, string[]> configured = new(StringComparer.OrdinalIgnoreCase);

    public InspectionScope(IConfiguration configuration, ILogger<InspectionScope> logger)
    {
        foreach (IConfigurationSection host in configuration.GetSection("Proxy:InspectOnly").GetChildren())
        {
            string[] paths = host.Get<string[]>() ?? [];

            if (paths.Length == 0)
                continue;

            scoped.Add((new HostPattern([host.Key]), paths));
            configured[host.Key] = paths;

            logger.LogInformation(
                "On {Host} only {Paths} are inspected; every other path is forwarded untouched.",
                host.Key,
                string.Join(", ", paths));
        }
    }

    /// <summary>The entries as configured, host to paths, for the dashboard's hello.</summary>
    public IReadOnlyDictionary<string, string[]> Scoped => configured;

    /// <summary>
    /// Whether the body of an exchange with <paramref name="destination"/> may be rewritten.
    ///
    /// True for any host with no entry, which is the default and leaves the rest of the proxy
    /// behaving exactly as it did.
    /// </summary>
    public bool Allows(Uri destination)
    {
        foreach ((HostPattern host, string[] paths) in scoped)
        {
            if (!host.Covers(destination.Host))
                continue;

            return paths.Any(path =>
                destination.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }
}
