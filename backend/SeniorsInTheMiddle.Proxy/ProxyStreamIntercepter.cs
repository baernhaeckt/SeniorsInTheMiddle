namespace backend;

public class ProxyStreamIntercepter(Stream SourceStream, Stream DestinationStream) : IStreamProxy
{
    public Task ProxyAsync()
    {
        return Task.WhenAll(
            SourceStream.CopyToAsync(DestinationStream),
            DestinationStream.CopyToAsync(SourceStream)
        );
    }
}

public interface IStreamProxy
{
    Task ProxyAsync();
}

public interface IStreamProxyFactory
{
    IStreamProxy Create(Stream sourceStream, Stream destinationStream);
}

public class StreamProxyFactory : IStreamProxyFactory
{
    public IStreamProxy Create(Stream sourceStream, Stream destinationStream) => 
        new ProxyStreamIntercepter(sourceStream, destinationStream);
}