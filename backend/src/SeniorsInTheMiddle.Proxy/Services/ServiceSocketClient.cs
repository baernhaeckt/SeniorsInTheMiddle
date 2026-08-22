using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;

namespace SeniorsInTheMiddle.Proxy.Services;

/// <summary>
/// Client for the python service runtime: length-prefixed JSON frames over a
/// unix socket. Requests are correlated by id, so several calls can be in
/// flight on a single connection.
/// </summary>
public sealed class ServiceSocketClient : IAsyncDisposable
{
    private const int HeaderSize = 4;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readLoop;
    private readonly int _maxFrameBytes;

    private int _nextId;

    private ServiceSocketClient(Socket socket, int maxFrameBytes)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _maxFrameBytes = maxFrameBytes;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Connects to <paramref name="socketPath"/>, waiting for the service to come up.</summary>
    public static async Task<ServiceSocketClient> ConnectAsync(
        string socketPath,
        TimeSpan? timeout = null,
        int maxFrameBytes = 8 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                return new ServiceSocketClient(socket, maxFrameBytes);
            }
            catch (SocketException ex)
            {
                last = ex;
                socket.Dispose();
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new TimeoutException($"Could not connect to {socketPath}.", last);
    }

    /// <summary>Calls <paramref name="method"/> and returns the <c>result</c> element.</summary>
    public async Task<JsonElement> CallAsync(
        string method,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var frame = JsonSerializer.SerializeToUtf8Bytes(
            new { id, method, payload = payload ?? new { } },
            SerializerOptions);

        try
        {
            await SendFrameAsync(frame, cancellationToken);
            await using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                return await completion.Task;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendFrameAsync(byte[] body, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(header, cancellationToken);
            await _stream.WriteAsync(body, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[HeaderSize];
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _shutdown.Token);
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length <= 0 || length > _maxFrameBytes)
                {
                    throw new InvalidDataException($"Invalid frame length {length}.");
                }

                var body = new byte[length];
                await _stream.ReadExactlyAsync(body, _shutdown.Token);
                Dispatch(body);
            }
        }
        catch (OperationCanceledException)
        {
            // disposing
        }
        catch (EndOfStreamException)
        {
            FailPending(new IOException("The service closed the connection."));
        }
        catch (Exception ex)
        {
            FailPending(ex);
        }
    }

    private void Dispatch(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("id", out var idElement))
        {
            return;
        }

        var id = idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()!
            : idElement.ToString();

        if (!_pending.TryRemove(id, out var completion))
        {
            return;
        }

        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
        {
            completion.TrySetResult(root.TryGetProperty("result", out var result)
                ? result.Clone()
                : default);
            return;
        }

        var error = root.GetProperty("error");
        completion.TrySetException(new ServiceCallException(
            error.TryGetProperty("code", out var code) ? code.GetString() ?? "unknown" : "unknown",
            error.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty,
            error.TryGetProperty("details", out var details) ? details.Clone() : null));
    }

    private void FailPending(Exception exception)
    {
        foreach (var (id, completion) in _pending)
        {
            completion.TrySetException(exception);
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
            // already gone
        }

        await _stream.DisposeAsync();
        _socket.Dispose();

        try
        {
            await _readLoop;
        }
        catch
        {
            // the read loop is expected to fault while tearing down
        }

        FailPending(new ObjectDisposedException(nameof(ServiceSocketClient)));
        _shutdown.Dispose();
        _writeLock.Dispose();
    }

    /// <summary>Convenience wrapper that deserializes the result.</summary>
    public async Task<T?> CallAsync<T>(string method, object? payload = null, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(method, payload, cancellationToken);
        return result.Deserialize<T>(SerializerOptions);
    }

    public static string Describe(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? "<undefined>" : element.GetRawText();
}

/// <summary>An error response returned by the python service.</summary>
public sealed class ServiceCallException(string code, string message, JsonElement? details = null)
    : Exception($"{code}: {message}")
{
    public string Code { get; } = code;

    public string ServiceMessage { get; } = message;

    public JsonElement? Details { get; } = details;
}
