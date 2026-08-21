using System.IO.Pipelines;

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
