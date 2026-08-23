using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Backend.Tests.Unit;

/// <summary>
/// A unix socket that speaks the python runtime's framing well enough to be pinged, for
/// tests about what the proxy does when a service answers, stalls, or goes away.
///
/// It is deliberately a real socket rather than a seam in the code. Everything worth
/// testing here -- reconnecting after the daemon bounced, a call that outlives its timeout,
/// a health check that has to tell "disabled" from "unreachable" -- is about the socket
/// itself, and a stubbed client would assert the stub.
/// </summary>
internal sealed class StubPythonService : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Socket> _accepted = [];
    private readonly Task _serving;
    private readonly TimeSpan _replyDelay;
    private readonly List<ServiceRequest> _requests = [];

    private int _connectionCount;

    private StubPythonService(string socketPath, TimeSpan replyDelay)
    {
        SocketPath = socketPath;
        _replyDelay = replyDelay;

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(8);

        _serving = Task.Run(AcceptLoopAsync);
    }

    public string SocketPath { get; }

    /// <summary>How many times a client has connected, so a reconnect can be observed.</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>
    /// What the proxy actually put on the wire, in order. The python side reads snake_case
    /// keys, and nothing on the C# side of a rename would notice if they stopped matching.
    /// </summary>
    public IReadOnlyList<ServiceRequest> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    /// <summary>
    /// The <c>result</c> to answer a given method with, as raw JSON. A method with no entry
    /// gets the generic ok reply, which is what the runtime's own <c>$ping</c> amounts to.
    /// </summary>
    public Dictionary<string, string> Results { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Starts a service that answers every call at once.
    /// </summary>
    public static StubPythonService Start() => new(FakeService.ShortSocketPath(), TimeSpan.Zero);

    /// <summary>
    /// Starts a service that accepts calls and answers them <paramref name="replyDelay"/>
    /// late, for the case where the daemon is slow rather than gone.
    /// </summary>
    public static StubPythonService StartSlow(TimeSpan replyDelay)
        => new(FakeService.ShortSocketPath(), replyDelay);

    /// <summary>
    /// Closes every accepted connection without closing the listener, the way supervisord
    /// restarting the daemon looks from this side: the socket breaks, the path stays.
    /// </summary>
    public void DropConnections()
    {
        lock (_accepted)
        {
            foreach (Socket connection in _accepted)
            {
                try
                {
                    connection.Shutdown(SocketShutdown.Both);
                }
                catch (SocketException)
                {
                    // Already gone; the dispose below is what matters.
                }

                connection.Dispose();
            }

            _accepted.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Dispose();
        DropConnections();

        try
        {
            await _serving;
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }

        _shutdown.Dispose();
        File.Delete(SocketPath);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await _listener.AcceptAsync(_shutdown.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            Interlocked.Increment(ref _connectionCount);
            lock (_accepted)
                _accepted.Add(connection);

            _ = Task.Run(() => ServeAsync(connection));
        }
    }

    private async Task ServeAsync(Socket connection)
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                byte[] request = await FakeService.ReadFrameAsync(connection);

                if (_replyDelay > TimeSpan.Zero)
                    await Task.Delay(_replyDelay, _shutdown.Token);

                await FakeService.WriteFrameAsync(connection, Answer(request));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException
            or SocketException or ObjectDisposedException)
        {
            // The client went away or the test tore the connection down.
        }
    }

    /// <summary>
    /// Answers one request, recording what it was.
    ///
    /// Input:  {"id":"1","method":"$ping","payload":{}}
    /// Output: {"id":"1","ok":true,"result":{"pong":true,"method":"$ping"}}
    ///
    /// A method listed in <see cref="Results"/> gets that raw JSON as its result instead.
    /// </summary>
    private byte[] Answer(byte[] request)
    {
        using JsonDocument document = JsonDocument.Parse(request);
        string id = document.RootElement.GetProperty("id").GetString()!;
        string method = document.RootElement.GetProperty("method").GetString()!;
        string payload = document.RootElement.TryGetProperty("payload", out JsonElement body)
            ? body.GetRawText()
            : "{}";

        lock (_requests)
            _requests.Add(new ServiceRequest(method, payload));

        string result = Results.TryGetValue(method, out string? configured)
            ? configured
            : $$$"""{"pong":true,"method":"{{{method}}}"}""";

        return Encoding.UTF8.GetBytes($$$"""{"id":"{{{id}}}","ok":true,"result":{{{result}}}}""");
    }
}

/// <summary>One call as the service saw it: the verb, and the payload's raw JSON.</summary>
internal sealed record ServiceRequest(string Method, string Payload);
