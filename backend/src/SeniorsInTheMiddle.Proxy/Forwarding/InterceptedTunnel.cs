namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Marks a request as having arrived inside an intercepted TLS tunnel, and carries the
/// authority the client named in its CONNECT.
///
/// <see cref="ConnectProxyMiddleware"/> sets it on the connection and every request on that
/// connection reads it back from <see cref="HttpContext.Features"/>, because Kestrel exposes
/// a connection's features to the requests it carries.
///
/// It exists because inside a tunnel the client believes it is talking to the origin server:
/// it sends origin-form targets ("GET /orders") and a Host header, and no request line says
/// which host the tunnel was opened to. This is the only record of it.
/// </summary>
interface IInterceptedTunnel
{
    /// <summary>Host and port exactly as the CONNECT request line gave them, e.g.
    /// <c>api.example.com:443</c>.</summary>
    string Authority { get; }
}

sealed record InterceptedTunnel(string Authority) : IInterceptedTunnel;
