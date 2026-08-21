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


        Task clientToDestination = clientInput.CopyToAsync(_remote, tunnelCancellation.Token);

        _logger.LogInformation("Waiting for the first chunk of data from the remote server...");
        MemoryStream response = await ReadCompletedChunkAsync(_remote, tunnelCancellation.Token);

        Task destinationToClient = response.CopyToAsync(clientOutput, tunnelCancellation.Token);
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

    public async Task<MemoryStream> ReadCompletedChunkAsync(Stream stream, CancellationToken cancellationToken)
    {
        MemoryStream memoryStream = new();
        byte[] buffer = new byte[8192];
        int bytesRead;

        while (true)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            try
            {
                bytesRead = await stream.ReadAsync(buffer.AsMemory(), cts.Token);

                if (bytesRead == 0)
                    break;

                await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        memoryStream.Position = 0;
        return memoryStream;
    }
}