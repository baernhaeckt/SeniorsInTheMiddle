namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// A set of host names written the way an operator writes them, matched the way DNS means them.
///
/// One entry covers the name and everything under it: "example.com" matches "example.com" and
/// "api.example.com". The dot is part of what is compared, so a match can never straddle a label
/// boundary -- "notexample.com" is a different domain, and anyone who can register it must not be
/// able to inherit a rule written for someone else's.
///
/// Shared rather than written twice. Both places that match a host decide whether traffic goes
/// uninspected, so the two must not be able to drift apart.
/// </summary>
sealed class HostPattern
{
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _suffixes = [];

    public bool IsEmpty => _exact.Count == 0;

    public HostPattern(IEnumerable<string> entries)
    {
        foreach (string entry in entries)
        {
            // "*.example.com" and "example.com" both mean the domain and everything under it.
            // The difference between them is not one anybody writing such a list intends to draw.
            string name = entry.Trim().TrimStart('*', '.').TrimEnd('.');

            if (name.Length == 0)
                continue;

            _exact.Add(name);
            _suffixes.Add($".{name}");
        }
    }

    public bool Covers(string host)
    {
        string name = host.Trim().TrimEnd('.');

        if (name.Length == 0)
            return false;

        if (_exact.Contains(name))
            return true;

        foreach (string suffix in _suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
