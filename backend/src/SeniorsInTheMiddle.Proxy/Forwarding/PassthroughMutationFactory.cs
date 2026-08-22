namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// The mutation that changes nothing.
///
/// It is the registered default so the forwarding path is complete and provable before any
/// detector exists: bodies are buffered, decompressed, offered and forwarded byte for byte, and
/// what leaves is identical to what arrived, integrity headers included. Swapping this for a
/// real implementation is the only change needed to start rewriting -- see
/// <see cref="IBodyMutationFactory"/>.
///
/// It holds nothing between the request and the response, so one instance serves every
/// exchange.
/// </summary>
sealed class PassthroughMutationFactory : IBodyMutationFactory, IExchangeBodyMutation
{
    public IExchangeBodyMutation CreateForExchange(Uri destination, IExchangeObserver observer) => this;

    public ValueTask<byte[]?> MutateRequestAsync(
        ReadOnlyMemory<byte> body,
        BodyDescriptor descriptor,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<byte[]?>(null);

    public ValueTask<byte[]?> MutateResponseAsync(
        ReadOnlyMemory<byte> body,
        BodyDescriptor descriptor,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<byte[]?>(null);
}
