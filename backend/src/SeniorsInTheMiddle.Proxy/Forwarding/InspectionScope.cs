namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Which paths on a host are worth rewriting, for hosts where the answer is "hardly any of them".
///
/// A site is not one thing. On chatgpt.com the prompt a user types goes to a handful of
/// endpoints under /backend-api/; everything else on that origin is the application itself --
/// scripts, feature flags, session polling, the bot challenge under /cdn-cgi/. None of it holds
/// anything a person typed, and every body scanned is a body that can come back subtly wrong: a
/// stand-in spliced into a settings JSON is a broken preference, spliced into a challenge
/// answer it is a page that never loads.
///
/// So a host listed here inverts the default: nothing is inspected except the paths named. That
/// is the safer direction to be wrong in -- the failure is "a body went unscanned" rather than
/// "a body was corrupted" -- and the endpoints that carry a prompt are few and knowable, while
/// everything that must not be touched is neither.
///
/// This is not <see cref="InterceptionBypass"/>. Traffic here is still decrypted, traced and
/// visible in the dashboard; the only question is whether a body reaches the mutation. A
/// bypassed host is not decrypted at all, and cannot be path-selective -- a CONNECT has no path.
/// </summary>
sealed class InspectionScope
{
    private readonly List<(HostPattern Host, string[] Paths)> _scoped = [];
    private readonly Dictionary<string, string[]> _configured = new(StringComparer.OrdinalIgnoreCase);

    public InspectionScope(IConfiguration configuration, ILogger<InspectionScope> logger)
    {
        foreach (IConfigurationSection host in configuration.GetSection("Proxy:InspectOnly").GetChildren())
        {
            string[] paths = host.Get<string[]>() ?? [];

            if (paths.Length == 0)
                continue;

            _scoped.Add((new HostPattern([host.Key]), paths));
            _configured[host.Key] = paths;

            logger.LogInformation(
                "On {Host} only {Paths} are inspected; every other path is forwarded untouched.",
                host.Key,
                string.Join(", ", paths));
        }
    }

    /// <summary>The entries as configured, host to paths, for the dashboard's hello.</summary>
    public IReadOnlyDictionary<string, string[]> Scoped => _configured;

    /// <summary>
    /// Whether the body of an exchange with <paramref name="destination"/> may be rewritten.
    ///
    /// True for any host with no entry, which is the default and leaves the rest of the proxy
    /// behaving exactly as it did.
    /// </summary>
    public bool Allows(Uri destination)
    {
        foreach ((HostPattern host, string[] paths) in _scoped)
        {
            if (!host.Covers(destination.Host))
                continue;

            return paths.Any(path =>
                destination.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }
}
