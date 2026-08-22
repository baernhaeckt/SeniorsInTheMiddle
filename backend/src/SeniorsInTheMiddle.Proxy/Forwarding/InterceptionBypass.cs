namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Hosts whose TLS is left alone: no MITM certificate, no decryption, no rewrite. A CONNECT to
/// one of these gets a raw byte tunnel to the origin instead of an intercepted one.
///
/// This exists for bot management, not for privacy. A site behind Cloudflare's managed challenge
/// scores the TLS handshake itself -- cipher order, extensions, ALPN, the HTTP/2 SETTINGS that
/// follow -- against the User-Agent the request claims. Interception replaces the browser's
/// handshake with this process's, so the two stop agreeing and the challenge can never be passed:
/// the client is handed a fresh challenge, answers it, is handed another, forever. Nothing in the
/// body pipeline can fix that, because the verdict is reached before a single HTTP byte is sent.
///
/// The cost is real and is the reason this list is short by default. A bypassed host is invisible
/// to the proxy: no PII is detected in what a device sends there and none is restored in what
/// comes back. Adding a destination here is deciding it will not be inspected at all.
/// </summary>
sealed class InterceptionBypass
{
    private readonly HostPattern hosts;

    public InterceptionBypass(IConfiguration configuration, ILogger<InterceptionBypass> logger)
    {
        // No list in code, deliberately. Every name here is a destination that goes uninspected,
        // and a default compiled into the binary is one an operator cannot see in their own
        // configuration and cannot remove. The shipped appsettings.json carries the entry instead,
        // where deleting it means what it looks like it means.
        string[] configured = configuration.GetSection("Proxy:BypassHosts").Get<string[]>() ?? [];

        hosts = new HostPattern(configured);

        if (!hosts.IsEmpty)
        {
            logger.LogInformation(
                "Not intercepting {Hosts}. Traffic to these is tunnelled unread and nothing in it is inspected.",
                string.Join(", ", configured));
        }
    }

    /// <summary>
    /// Whether <paramref name="host"/> is left unintercepted. An entry covers its subdomains --
    /// see <see cref="HostPattern"/>.
    /// </summary>
    public bool Covers(string host) => hosts.Covers(host);
}
