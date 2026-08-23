using System.Collections.Concurrent;
using System.Net;

namespace SeniorsInTheMiddle.Proxy.Telemetry;

/// <summary>
/// Turns a connection into something a person can recognise on a wall display.
///
/// The proxy has no idea who owns a device, so the label is the kind of device the
/// User-Agent claims to be plus the last part of its address — enough to tell two tablets
/// on the same network apart, and stable for as long as the lease is.
/// </summary>
sealed class ClientLabeler
{
    private readonly ConcurrentDictionary<string, string> _labels = new();

    public string Label(IPAddress? address, string? userAgent)
        => _labels.GetOrAdd(
            Identity(address, userAgent),
            _ => $"{DeviceKind(userAgent)} · {Suffix(Ip(address))}");

    /// <summary>
    /// The same device across every request it makes, as far as a proxy can tell — the address
    /// it connects from and the kind of thing its User-Agent claims to be.
    ///
    /// It is the key of <see cref="Label"/> rather than the label itself because two devices
    /// that differ only in the part of the address the label hides must not share one: the
    /// label is written to be read from across a room, and everything it drops on the way is
    /// exactly what tells those two apart.
    ///
    /// Input:  127.0.0.1, "... Windows ..."  -&gt; "Laptop|127.0.0.1"
    /// Input:  ::ffff:10.0.0.4, null         -&gt; "Device|10.0.0.4"
    /// </summary>
    public static string Identity(IPAddress? address, string? userAgent)
        => $"{DeviceKind(userAgent)}|{Ip(address)}";

    /// <summary>
    /// Kestrel reports IPv4 clients as ::ffff:127.0.0.1 on a dual-stack socket, which makes
    /// for an unreadable label.
    /// </summary>
    public static string Ip(IPAddress? address)
        => address is null
            ? "unknown"
            : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    private static string Suffix(string ip)
    {
        int lastDot = ip.LastIndexOf('.');
        if (lastDot >= 0)
            return ip[lastDot..];

        int lastColon = ip.LastIndexOf(':');
        return lastColon >= 0 ? ip[(lastColon + 1)..] : ip;
    }

    private static string DeviceKind(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "Device";

        if (userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("Tablet", StringComparison.OrdinalIgnoreCase))
            return "Tablet";

        if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
            return "Phone";

        if (userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("X11", StringComparison.OrdinalIgnoreCase))
            return "Laptop";

        return "Device";
    }
}
