namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// The mutation that changes nothing.
///
/// It is the registered default so the forwarding path is complete and provable before any
/// detector exists: bodies are buffered, offered, and forwarded byte for byte, and the
/// request that leaves is identical to the one that arrived, integrity headers included.
/// Swapping this for a real implementation is the only change needed to start rewriting --
/// see <see cref="IRequestBodyMutation"/>.
/// </summary>
sealed class PassthroughBodyMutation : IRequestBodyMutation
{
    public ValueTask<byte[]?> MutateAsync(
        ReadOnlyMemory<byte> body,
        RequestBodyDescriptor descriptor,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<byte[]?>(null);
}
