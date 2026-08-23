using System.IO.Pipelines;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>Builds <see cref="StreamProxy"/> instances, supplying each one its own logger.</summary>
sealed class StreamProxyFactory : IStreamProxyFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public StreamProxyFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IStreamProxy Create(IDuplexPipe client, Stream remote)
        => new StreamProxy(client, remote, _loggerFactory.CreateLogger<StreamProxy>());
}
