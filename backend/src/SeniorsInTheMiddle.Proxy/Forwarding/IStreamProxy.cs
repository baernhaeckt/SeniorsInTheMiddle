namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Copies bytes both ways between a client connection and its upstream peer until either
/// side hangs up. Used for CONNECT tunnels the proxy does not intercept.
/// </summary>
public interface IStreamProxy
{
    Task ProxyAsync(CancellationToken connectionClosed);
}
