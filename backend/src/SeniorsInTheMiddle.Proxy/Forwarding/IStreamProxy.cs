namespace SeniorsInTheMiddle.Proxy.Forwarding;

public interface IStreamProxy
{
    Task ProxyAsync(CancellationToken connectionClosed);
}
