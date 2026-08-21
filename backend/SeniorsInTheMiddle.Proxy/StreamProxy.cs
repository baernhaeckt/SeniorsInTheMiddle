using System.IO.Pipelines;

public class StreamProxy : IStreamProxy
{
    private readonly IDuplexPipe _client;
    private readonly Stream _remote;
    private readonly ILogger<StreamProxy> _logger;

    public StreamProxy(IDuplexPipe client, Stream remote, ILogger<StreamProxy> logger)
    {
        _client = client;
        _remote = remote;
        _logger = logger;
    }

    public async Task ProxyAsync(CancellationToken connectionClosed)
    {
        await using Stream clientInput = _client.Input.AsStream(leaveOpen: true);
        await using Stream clientOutput = _client.Output.AsStream(leaveOpen: true);
        using CancellationTokenSource tunnelCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(connectionClosed);

        Task clientToDestination = CopyChunksAsync(
            clientInput,
            _remote,
            "Client -> remote",
            tunnelCancellation.Token);

        Task destinationToClient = CopyChunksAsync(
            _remote,
            clientOutput,
            "Remote -> client",
            tunnelCancellation.Token);

        await Task.WhenAny(clientToDestination, destinationToClient);
        tunnelCancellation.Cancel();

        try
        {
            await Task.WhenAll(clientToDestination, destinationToClient);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
        }
    }

    private async Task CopyChunksAsync(
        Stream source,
        Stream destination,
        string direction,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];

        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (bytesRead == 0)
                return;

            using MemoryStream chunk = new(bytesRead);
            await chunk.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            chunk.Position = 0;

            _logger.LogInformation(
                "{Direction} ({ByteCount} bytes, Base64): {Data}",
                direction,
                bytesRead,
                Convert.ToBase64String(buffer, 0, bytesRead));

            await chunk.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
    }
}
