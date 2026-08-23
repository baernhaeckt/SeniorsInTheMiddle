using System.IO.Pipelines;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>Pairs a client pipe with its upstream stream into a running <see cref="IStreamProxy"/>.</summary>
public interface IStreamProxyFactory
{
    IStreamProxy Create(IDuplexPipe client, Stream remote);
}
