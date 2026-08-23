namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Destinations the proxy answers with a redirect instead of forwarding: a host, the path
/// prefixes on it, and where to send the client instead.
///
/// This is for traffic nobody at the device asked for. A compromised box behind the proxy
/// that fetches one YouTube watch page after another is running somebody's view farm, and
/// every one of those requests costs an upstream connection, a trace, and a row in the
/// dashboard. Answering locally with a 302 costs none of that, and pointing the 302 at one
/// well-known video is the cheapest way of making the dashboard say what is going on.
///
/// It is a blunt instrument, deliberately. There is no inspection behind it: a request that
/// matches never leaves this process, so whatever it carried is neither scanned nor
/// forwarded. Keep the paths narrow -- "/watch" on youtube.com, not the host.
///
/// A rule cannot loop. The target is checked against the rules too, and a request for the
/// target itself is forwarded normally, so a rickroll on youtube.com/watch still plays.
/// </summary>
sealed class Detours
{
    private readonly List<(HostPattern Host, string[] Paths, Uri Target)> _rules = [];

    public Detours(IConfiguration configuration, ILogger<Detours> logger)
    {
        foreach (IConfigurationSection host in configuration.GetSection("Proxy:Detours").GetChildren())
        {
            string[] paths = host.GetSection("Paths").Get<string[]>() ?? [];
            string? to = host["To"];

            if (paths.Length == 0 || !Uri.TryCreate(to, UriKind.Absolute, out Uri? target))
            {
                logger.LogWarning(
                    "Ignoring Proxy:Detours:{Host}: it needs at least one path under Paths and an absolute URL under To.",
                    host.Key);
                continue;
            }

            _rules.Add((new HostPattern([host.Key]), paths, target));

            logger.LogInformation(
                "Requests to {Host} under {Paths} are redirected to {Target} and never forwarded.",
                host.Key,
                string.Join(", ", paths),
                target);
        }
    }

    public bool IsEmpty => _rules.Count == 0;

    /// <summary>
    /// Where <paramref name="destination"/> is sent instead, or null when it is forwarded
    /// as usual -- the default for everything not listed, and for a target's own URL.
    /// </summary>
    public Uri? For(Uri destination)
    {
        foreach ((HostPattern host, string[] paths, Uri target) in _rules)
        {
            if (!host.Covers(destination.Host))
                continue;

            if (!paths.Any(path => destination.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase)))
                continue;

            // The one request that must get through, or the detour chases its own tail.
            if (Uri.Compare(
                    destination,
                    target,
                    UriComponents.HttpRequestUrl,
                    UriFormat.SafeUnescaped,
                    StringComparison.OrdinalIgnoreCase) == 0)
                return null;

            return target;
        }

        return null;
    }
}
