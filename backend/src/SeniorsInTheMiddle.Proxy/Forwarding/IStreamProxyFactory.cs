using System.IO.Pipelines;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

public interface IStreamProxyFactory
{
    IStreamProxy Create(IDuplexPipe client, Stream remote);
}
