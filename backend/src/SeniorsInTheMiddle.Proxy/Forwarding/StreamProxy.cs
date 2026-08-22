using System.IO.Pipelines;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Copies bytes both ways between a client and an origin without reading them, for a CONNECT
/// tunnel that turned out not to carry HTTP.
///
/// The payload is deliberately never decoded or logged. It is somebody's mail or database
/// session in the clear, and turning every chunk into a string would put those bytes in the
/// log and cap the tunnel's throughput at the logging sink's.
/// </summary>
public class StreamProxy : IStreamProxy
{
    private const int BufferBytes = 8192;

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

        // A client that half-closes its send side -- SMTP after QUIT, an upload framed by
        // close -- has said it is done asking, not that it is done listening. Cancelling the
        // other direction here would cut off the reply it is still waiting for, and the
        // truncation would be silent. So an orderly end of the client's stream only stops
        // that direction; everything else, the origin closing or either copy faulting, ends
        // the tunnel.
        if (clientToDestination.IsCompletedSuccessfully)
            await WaitQuietlyAsync(destinationToClient);

        tunnelCancellation.Cancel();

        await WaitQuietlyAsync(Task.WhenAll(clientToDestination, destinationToClient));
    }

    private static async Task WaitQuietlyAsync(Task copying)
    {
        try
        {
            await copying;
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
        byte[] buffer = new byte[BufferBytes];
        long total = 0;

        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (bytesRead == 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            await destination.FlushAsync(cancellationToken);
            total += bytesRead;
        }

        // Once per direction, and volume only: enough to tell a tunnel that carried nothing
        // from one that carried a gigabyte, without the payload it carried.
        _logger.LogDebug("{Direction} closed after {ByteCount} bytes.", direction, total);
    }
}
