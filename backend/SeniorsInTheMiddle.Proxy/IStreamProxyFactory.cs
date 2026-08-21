using System.IO.Pipelines;

public interface IStreamProxyFactory
{
    IStreamProxy Create(IDuplexPipe client, Stream remote);
}
