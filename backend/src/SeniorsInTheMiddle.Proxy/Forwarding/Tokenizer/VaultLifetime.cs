namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>
/// How long a client's stand-in map is kept and how many of them the process holds at once,
/// read from the <c>Proxy</c> configuration section.
///
/// Both numbers bound the same thing from different sides. The map is the one structure in the
/// process that pairs a fake value with the real one it hides, so every hour it lives is an hour
/// that pairing is available to anything that can read the process, and every client that keeps
/// one is another such pairing. Neither is a memory setting: a few thousand entries cost
/// nothing. They are how long the disclosure risk lasts and how far it spreads.
/// </summary>
/// <param name="Ttl">How long after its last use a client's map is dropped. Zero pins the map to
/// the single exchange that created it, which is the safest setting and the one that cannot
/// restore anything a site echoes back in a later request.</param>
/// <param name="MaxClients">How many client maps are held at once. Past this the
/// least-recently-used are dropped, whatever their age.</param>
public sealed record VaultLifetime(TimeSpan Ttl, int MaxClients)
{
    /// <summary>
    /// Two days: long enough that a chat opened yesterday still restores when its history is
    /// re-fetched this morning, short enough that a device left on a guest network is not
    /// carrying someone's real name into next week.
    /// </summary>
    public const int DefaultTtlHours = 48;

    /// <summary>
    /// The ceiling the setting is refused above. A map that never expires is a map that has to
    /// be reasoned about as permanent storage of exactly the values this proxy exists to hide,
    /// and nothing here is built to be that.
    /// </summary>
    public const int MaxTtlHours = 24 * 14;

    /// <summary>Comfortably above the devices on a household or classroom network, and far
    /// below what an open proxy scanned from the internet would try to create.</summary>
    public const int DefaultMaxClients = 512;

    public static VaultLifetime From(IConfiguration configuration)
    {
        int ttlHours = configuration.GetValue("Proxy:AnonymizerTtlHours", DefaultTtlHours);
        int maxClients = configuration.GetValue("Proxy:MaxAnonymizerClients", DefaultMaxClients);

        if (ttlHours < 0)
        {
            throw new InvalidOperationException(
                $"Proxy:AnonymizerTtlHours ({ttlHours}) cannot be negative. Use 0 to keep a stand-in map for one exchange only.");
        }

        if (ttlHours > MaxTtlHours)
        {
            throw new InvalidOperationException(
                $"Proxy:AnonymizerTtlHours ({ttlHours}) is above the {MaxTtlHours} hour ceiling. "
                + "The map pairs every hidden value with the real one, so its lifetime is how long that pairing exists.");
        }

        if (maxClients < 1)
        {
            throw new InvalidOperationException(
                $"Proxy:MaxAnonymizerClients ({maxClients}) must be at least 1.");
        }

        return new VaultLifetime(TimeSpan.FromHours(ttlHours), maxClients);
    }
}
