namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Destinations the proxy refuses outright: a host and the path prefixes on it. A matching
/// request is answered 403 from this process, is never forwarded, and is never traced, so it
/// does not reach the dashboard at all.
///
/// The silence is the point. <see cref="Detours"/> keeps the request visible, which is right
/// for something an operator still wants to see happening; this is for traffic that has been
/// seen, understood, and would only bury everything else if it kept appearing -- a box behind
/// the proxy pulling one YouTube watch page after another for somebody's view farm. Nothing
/// about it is worth a row, and a thousand rows of it hide the one exchange that matters.
///
/// Blocked is blocked for every device behind the proxy. Keep the paths narrow: "/watch" on
/// youtube.com, not the host, unless the host is what is meant.
/// </summary>
sealed class Blocklist
{
    private readonly List<(HostPattern Host, string[] Paths)> _rules = [];

    public Blocklist(IConfiguration configuration, ILogger<Blocklist> logger)
    {
        foreach (IConfigurationSection host in configuration.GetSection("Proxy:Blocked").GetChildren())
        {
            string[] paths = host.Get<string[]>() ?? [];

            if (paths.Length == 0)
                continue;

            _rules.Add((new HostPattern([host.Key]), paths));

            logger.LogInformation(
                "Requests to {Host} under {Paths} are refused and never forwarded or shown.",
                host.Key,
                string.Join(", ", paths));
        }
    }

    public bool IsEmpty => _rules.Count == 0;

    /// <summary>Whether <paramref name="destination"/> is refused. False for everything not listed.</summary>
    public bool Covers(Uri destination)
    {
        foreach ((HostPattern host, string[] paths) in _rules)
        {
            if (!host.Covers(destination.Host))
                continue;

            if (paths.Any(path => destination.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }
}
