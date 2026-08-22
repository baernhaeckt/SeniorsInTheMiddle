namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// The three listeners this app opens, read from the <c>Proxy</c> configuration section.
///
/// Each port has exactly one job: devices are pointed at a proxy port, the dashboard and
/// anything human-facing at the API port. Which listener a request arrived on decides how
/// it is treated.
/// </summary>
/// <param name="HttpProxy">Proxy traffic in the clear: CONNECT tunnels and absolute-form HTTP.</param>
/// <param name="HttpsProxy">The same protocol inside TLS, for clients configured with an
/// <c>HTTPS</c> proxy. Zero or less turns the listener off.</param>
/// <param name="Api">The WebAPI, Swagger and the telemetry hub. Never proxies.</param>
sealed record ProxyPorts(int HttpProxy, int HttpsProxy, int Api)
{
    /// <summary>
    /// Reads the ports and refuses a configuration that cannot work. Kestrel would fail to
    /// bind a duplicate port anyway, but "address already in use" says nothing about which
    /// two settings collided.
    /// </summary>
    public static ProxyPorts From(IConfiguration configuration)
    {
        ProxyPorts ports = new(
            configuration.GetValue("Proxy:HttpPort", 3128),
            configuration.GetValue("Proxy:HttpsPort", 3127),
            configuration.GetValue("Proxy:ApiPort", 8080));

        if (ports.HttpProxy <= 0)
            throw new InvalidOperationException("Proxy:HttpPort must be a port number; the plain proxy listener cannot be turned off.");

        if (ports.Api <= 0)
            throw new InvalidOperationException("Proxy:ApiPort must be a port number; the API listener cannot be turned off.");

        if (ports.HttpProxy == ports.Api || ports.HttpsProxy == ports.Api || ports.HttpProxy == ports.HttpsProxy)
        {
            throw new InvalidOperationException(
                $"Proxy:HttpPort ({ports.HttpProxy}), Proxy:HttpsPort ({ports.HttpsProxy}) and "
                + $"Proxy:ApiPort ({ports.Api}) must all differ. Each listener serves a different role.");
        }

        return ports;
    }

    /// <summary>
    /// Whether a request arrived on one of the proxy listeners.
    ///
    /// TestServer reports a local port of 0 and <see cref="HttpProxy"/> is validated above
    /// to be a real port, so a test host is never mistaken for a proxy listener.
    /// </summary>
    public bool IsProxyListener(int port) => port == HttpProxy || (HttpsProxy > 0 && port == HttpsProxy);
}
