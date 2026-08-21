using System.Net;
using System.Net.Sockets;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Every name this app answers to. Feeds the HTTPS certificate's subject alternative
/// names, and stops an absolute-form request aimed at the API from being proxied
/// straight back to us.
/// </summary>
sealed class SelfHostNames
{
    private readonly HashSet<string> lookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> ordered = [];

    /// <summary>Ordered, de-duplicated. The first entry becomes the certificate's subject.</summary>
    public IReadOnlyList<string> Names => ordered;

    public SelfHostNames(IConfiguration configuration, ILogger<SelfHostNames> logger)
    {
        Add("localhost");

        // Whatever the deployment is actually reached by: a DNS name, a LAN address.
        foreach (string configured in configuration.GetSection("Proxy:HostNames").Get<string[]>() ?? [])
        {
            Add(configured);
        }

        Add("127.0.0.1");
        Add("::1");

        try
        {
            string machine = Dns.GetHostName();
            Add(machine);
            foreach (IPAddress address in Dns.GetHostAddresses(machine))
            {
                if (address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    Add(address.ToString());
            }
        }
        catch (SocketException exception)
        {
            // A container with no resolvable host name still serves on localhost.
            logger.LogDebug(exception, "Could not resolve the local host name.");
        }

        logger.LogInformation("Answering as {HostNames}.", string.Join(", ", ordered));
    }

    public bool Contains(string host) => lookup.Contains(host);

    private void Add(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length > 0 && lookup.Add(trimmed))
            ordered.Add(trimmed);
    }
}
