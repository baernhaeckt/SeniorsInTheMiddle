using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Connections;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Turns a client's CONNECT into a TLS session this proxy can read.
///
/// What happens to the decrypted bytes then depends on what they are. HTTP is handed back to
/// Kestrel by swapping the connection's transport for the decrypted stream, so the requests
/// inside the tunnel run through the ordinary pipeline and get forwarded, inspected and
/// rewritten exactly like plaintext ones. Anything else is copied through byte for byte,
/// because a CONNECT is not a promise that HTTP follows and an HTTP parser would answer 400
/// to a mail or database session that used to work.
/// </summary>
sealed class ConnectProxyMiddleware
{
    /// <summary>What the first decrypted bytes turned out to be.</summary>
    private enum TunnelPayload
    {
        /// <summary>The client went away before sending anything.</summary>
        None,

        /// <summary>An HTTP/1.x request line. Goes back to Kestrel.</summary>
        Http,

        /// <summary>Something else entirely. Gets a byte tunnel.</summary>
        Opaque,
    }

    /// <summary>
    /// Largest request head this will buffer while looking for its end.
    ///
    /// A CONNECT is a request line and a handful of headers. Anything past this is either not
    /// a CONNECT or not one worth answering, and without a ceiling a client that sends headers
    /// forever would have them held in memory forever.
    /// </summary>
    private const int MaxHeadBytes = 8 * 1024;

    /// <summary>What ends a request head, and therefore where the client's TLS bytes start.</summary>
    private static ReadOnlySpan<byte> HeadTerminator => "\r\n\r\n"u8;

    /// <summary>
    /// How long the whole head has to arrive.
    ///
    /// Matches Kestrel's default RequestHeadersTimeout, which is the deadline a partial request
    /// used to inherit by being handed straight to it. Buffering the head here instead takes
    /// that deadline away, and nothing else would end a connection that sends half a CONNECT and
    /// then goes quiet: enough of those and the process is holding connections it will never
    /// hear from again.
    /// </summary>
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The buffered request head, and the position just past its terminator.</summary>
    internal readonly record struct RequestHead(ReadOnlySequence<byte> Buffer, SequencePosition? End);

    private readonly IStreamProxyFactory streamProxyFactory;
    private readonly MitmCertificateProvider certificateProvider;
    private readonly InterceptionBypass bypass;
    private readonly ILogger<ConnectProxyMiddleware> logger;

    public ConnectProxyMiddleware(
        IStreamProxyFactory streamProxyFactory,
        MitmCertificateProvider certificateProvider,
        InterceptionBypass bypass,
        ILogger<ConnectProxyMiddleware> logger)
    {
        this.streamProxyFactory = streamProxyFactory;
        this.certificateProvider = certificateProvider;
        this.bypass = bypass;
        this.logger = logger;
    }

    public async Task InvokeAsync(ConnectionContext connection, ConnectionDelegate next)
    {
        PipeReader input = connection.Transport.Input;
        PipeWriter output = connection.Transport.Output;

        if (await ReadHeadAsync(input, HeadTimeout, connection.ConnectionClosed) is not { } head)
        {
            logger.LogDebug(
                "No complete request head from {Endpoint} within {Timeout}.",
                connection.RemoteEndPoint,
                HeadTimeout);

            return;
        }

        ReadOnlySequence<byte> buffer = head.Buffer;
        SequencePosition? headerEnd = head.End;

        if (headerEnd is null)
        {
            input.AdvanceTo(buffer.Start, buffer.End);
            await next(connection);
            return;
        }

        string headerText = Encoding.ASCII.GetString(buffer.Slice(0, headerEnd.Value).ToArray());
        string requestLine = headerText.Split("\r\n", 2, StringSplitOptions.None)[0];
        if (!requestLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
        {
            input.AdvanceTo(buffer.Start, buffer.Start);
            await next(connection);
            return;
        }

        if (!TryParseConnectTarget(requestLine, out string host, out int port))
        {
            await WriteProxyErrorAsync(output, "400 Bad Request");
            input.AdvanceTo(buffer.End);
            return;
        }

        input.AdvanceTo(headerEnd.Value, headerEnd.Value);

        // The tunnel is confirmed before the destination is known to be reachable, because
        // whether we even want a connection to it depends on bytes that only arrive after the
        // handshake. For forwarded traffic that is the better order anyway: an unreachable
        // origin becomes a 502 the client reads inside the tunnel, rather than a refused
        // CONNECT it has to guess the meaning of.
        await output.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));

        // Before the handshake, which is the only place this decision can be made. Once a
        // certificate of ours has been offered the client's own ClientHello is already spent, and
        // the origin will never see it -- see InterceptionBypass.
        //
        // Host and port are all a CONNECT carries, so this is necessarily all-or-nothing for a
        // destination: there is no path here to be selective about, and none will exist until
        // after the very handshake being skipped.
        if (bypass.Covers(host))
        {
            await TunnelVerbatimAsync(connection, host, port);
            return;
        }

        try
        {
            await using Stream clientStream = connection.Transport.Input.AsStream(leaveOpen: true);
            await using Stream clientOutputStream = connection.Transport.Output.AsStream(leaveOpen: true);
            using SslStream clientTls = new(
                new DuplexStream(clientStream, clientOutputStream),
                leaveInnerStreamOpen: true);

            // Not disposed here: the provider owns it and hands the same instance to every
            // connection for this host (see MitmCertificateProvider.GetServerCertificate).
            X509Certificate2 serverCertificate = certificateProvider.GetServerCertificate(host);
            await clientTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,

                // Stated rather than left to chance. Everything past this point reads HTTP/1.1:
                // the listener is pinned to it and the sniffing below only recognises it. With
                // no list at all a client that offered only h2 would assume it had been
                // accepted and speak a protocol nothing here parses; refusing the handshake is
                // the honest answer to that.
                ApplicationProtocols = [SslApplicationProtocol.Http11],
            }, connection.ConnectionClosed);

            IDuplexPipe decrypted = new StreamDuplexPipe(clientTls);
            switch (await SniffAsync(decrypted.Input, connection.ConnectionClosed))
            {
                case TunnelPayload.None:
                    return;

                case TunnelPayload.Http:
                    // Kestrel reads the connection's transport, so replacing it hands the
                    // decrypted stream to the HTTP layer and the requests inside the tunnel
                    // reach the ordinary pipeline.
                    connection.Features.Set<IInterceptedTunnel>(new InterceptedTunnel($"{host}:{port}"));
                    connection.Transport = decrypted;
                    await next(connection);
                    return;

                default:
                    await TunnelOpaqueAsync(decrypted, host, port, connection.ConnectionClosed);
                    return;
            }
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
        {
            logger.LogDebug(ex, "HTTPS interception ended for {Host}:{Port}", host, port);
        }
    }

    /// <summary>
    /// Copies bytes between the client and the origin without reading them, for a tunnel that
    /// turned out not to carry HTTP.
    /// </summary>
    /// <summary>
    /// Copies the tunnel's bytes through untouched, TLS records and all.
    ///
    /// The difference from <see cref="TunnelOpaqueAsync"/> is which handshake reaches the origin.
    /// That one has already terminated the client's TLS and opens a second session of its own, so
    /// the origin sees this process; this one runs before any of that, so what goes out is the
    /// client's own ClientHello, cipher list, ALPN and HTTP/2 preface. That is the entire point of
    /// the bypass, and it is why nothing here may inspect, buffer or re-frame what passes.
    /// </summary>
    private async Task TunnelVerbatimAsync(ConnectionContext connection, string host, int port)
    {
        using TcpClient tcpClient = new();

        try
        {
            await tcpClient.ConnectAsync(host, port, connection.ConnectionClosed);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            // The 200 has gone out already, so the client is waiting inside a tunnel and there is
            // no status line left to answer with. Closing is the only signal available.
            logger.LogDebug(ex, "Could not reach {Host}:{Port} for an unintercepted tunnel.", host, port);

            return;
        }

        logger.LogDebug("Tunnelling {Host}:{Port} unintercepted.", host, port);

        await using NetworkStream upstream = tcpClient.GetStream();

        await streamProxyFactory
            .Create(connection.Transport, upstream)
            .ProxyAsync(connection.ConnectionClosed);
    }

    private async Task TunnelOpaqueAsync(
        IDuplexPipe decrypted,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        using TcpClient tcpClient = new();

        try
        {
            await tcpClient.ConnectAsync(host, port, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            // The tunnel was confirmed already, so there is no status line left to answer with
            // and the client is inside TLS. Closing is the only signal available.
            logger.LogDebug(ex, "Could not reach {Host}:{Port} for a tunnelled connection.", host, port);
            return;
        }

        using SslStream upstreamTls = new(tcpClient.GetStream(), leaveInnerStreamOpen: false);
        await upstreamTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, cancellationToken);

        await streamProxyFactory.Create(decrypted, upstreamTls).ProxyAsync(cancellationToken);
    }

    /// <summary>
    /// Looks at the first decrypted bytes and puts them back.
    ///
    /// The examined position must be the start of the buffer. Reporting anything further tells
    /// the reader we are waiting for bytes we have not seen yet, and the next read then blocks
    /// on a client that is itself waiting for our reply.
    /// </summary>
    private static async ValueTask<TunnelPayload> SniffAsync(
        PipeReader reader,
        CancellationToken cancellationToken)
    {
        ReadResult result = await reader.ReadAsync(cancellationToken);

        try
        {
            if (result.Buffer.IsEmpty)
                return result.IsCompleted ? TunnelPayload.None : TunnelPayload.Opaque;

            return LooksLikeHttpRequestLine(result.Buffer) ? TunnelPayload.Http : TunnelPayload.Opaque;
        }
        finally
        {
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.Start);
        }
    }

    /// <summary>
    /// Whether the buffer opens with an HTTP/1.x request line, which is a method, a target and
    /// a version separated by single spaces.
    ///
    /// Input:  "GET /health HTTP/1.1\r\nHost: ..."   -> true
    /// Input:  "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"    -> false, nothing downstream speaks HTTP/2
    /// Input:  "EHLO mail.example.com\r\n"           -> false
    /// Input:  "\x16\x03\x01..." (nested TLS)        -> false
    /// </summary>
    private static bool LooksLikeHttpRequestLine(ReadOnlySequence<byte> buffer)
    {
        // A request line longer than this is not one we would forward anyway, and the check
        // only needs its last token.
        const int MaxRequestLineBytes = 256;

        string start = Encoding.ASCII.GetString(
            buffer.Slice(0, Math.Min(buffer.Length, MaxRequestLineBytes)).ToArray());

        int lineEnd = start.IndexOf('\r');
        string[] parts = (lineEnd < 0 ? start : start[..lineEnd]).Split(' ');

        return parts.Length == 3 && parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Buffers until the request head is whole, and hands back what was read.
    ///
    /// A single read is not enough: a CONNECT carrying Proxy-Authorization or a long User-Agent
    /// routinely arrives in two segments, and giving up on the first would hand the half-read
    /// request to Kestrel, which answers 400 and leaves the client with no tunnel at all.
    ///
    /// Returns null when <paramref name="timeout"/> passed or the client went away, neither of
    /// which leaves anything worth answering.
    /// </summary>
    internal static async ValueTask<RequestHead?> ReadHeadAsync(
        PipeReader input,
        TimeSpan timeout,
        CancellationToken connectionClosed)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(connectionClosed);

        deadline.CancelAfter(timeout);

        try
        {
            while (true)
            {
                ReadResult readResult = await input.ReadAsync(deadline.Token);
                ReadOnlySequence<byte> buffer = readResult.Buffer;
                SequencePosition? headerEnd = FindHeaderEnd(buffer);

                if (headerEnd is not null || readResult.IsCompleted || buffer.Length >= MaxHeadBytes)
                    return new RequestHead(buffer, headerEnd);

                // Nothing consumed, everything examined: this asks for more bytes rather than
                // being handed the same incomplete head again.
                input.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the request head ends, or null while it is still incomplete.
    ///
    /// Input:  "CONNECT host:443 HTTP/1.1\r\nHost: host:443\r\n\r\n\x16\x03..."
    ///          -> the position of the \x16, the first byte of the client's TLS handshake
    /// Input:  "CONNECT host:443 HTTP/1.1\r\nHost: ho"  -> null, read more
    ///
    /// Scans the sequence in place: this runs once per read while the head is being collected,
    /// and copying the whole buffer out each time to look for four bytes is a copy per segment.
    /// </summary>
    private static SequencePosition? FindHeaderEnd(ReadOnlySequence<byte> buffer)
    {
        SequenceReader<byte> reader = new(buffer);

        return reader.TryReadTo(out ReadOnlySequence<byte> _, HeadTerminator, advancePastDelimiter: true)
            ? reader.Position
            : null;
    }

    private static bool TryParseConnectTarget(string requestLine, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !Uri.TryCreate($"tcp://{parts[1]}", UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535)
        {
            return false;
        }

        host = uri.Host;
        port = uri.Port;
        return true;
    }

    private static Task WriteProxyErrorAsync(PipeWriter output, string status)
        => output.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")).AsTask();
}
