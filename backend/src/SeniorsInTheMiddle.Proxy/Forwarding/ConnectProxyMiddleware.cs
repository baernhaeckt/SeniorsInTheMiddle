using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using Microsoft.AspNetCore.Connections;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

sealed class ConnectProxyMiddleware
{
    private readonly IStreamProxyFactory streamProxyFactory;
    private readonly MitmCertificateProvider certificateProvider;
    private readonly ILogger<ConnectProxyMiddleware> logger;

    public ConnectProxyMiddleware(
        IStreamProxyFactory streamProxyFactory,
        MitmCertificateProvider certificateProvider,
        ILogger<ConnectProxyMiddleware> logger)
    {
        this.streamProxyFactory = streamProxyFactory;
        this.certificateProvider = certificateProvider;
        this.logger = logger;
    }

    public async Task InvokeAsync(ConnectionContext connection, ConnectionDelegate next)
    {
        PipeReader input = connection.Transport.Input;
        PipeWriter output = connection.Transport.Output;
        ReadResult readResult = await input.ReadAsync(connection.ConnectionClosed);
        ReadOnlySequence<byte> buffer = readResult.Buffer;
        SequencePosition? headerEnd = FindHeaderEnd(buffer);

        if (headerEnd is null || buffer.Length == 0)
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

        if (!TryParseConnectTarget(requestLine, out string? host, out int port))
        {
            await WriteProxyErrorAsync(output, "400 Bad Request");
            input.AdvanceTo(buffer.End);
            return;
        }

        using (TcpClient tcpClient = new())
        {
            try
            {
                await tcpClient.ConnectAsync(host, port, connection.ConnectionClosed);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                if (!connection.ConnectionClosed.IsCancellationRequested)
                {
                    await WriteProxyErrorAsync(output, "502 Bad Gateway");
                }

                input.AdvanceTo(buffer.End);
                return;
            }

            input.AdvanceTo(headerEnd.Value, headerEnd.Value);
            await output.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));

            try
            {
                await using Stream clientStream = connection.Transport.Input.AsStream(leaveOpen: true);
                await using Stream clientOutputStream = connection.Transport.Output.AsStream(leaveOpen: true);
                using SslStream clientTls = new(
                    new DuplexStream(clientStream, clientOutputStream),
                    leaveInnerStreamOpen: true);
                using System.Security.Cryptography.X509Certificates.X509Certificate2 serverCertificate =
                    certificateProvider.CreateServerCertificate(host);
                await clientTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false
                }, connection.ConnectionClosed);

                using SslStream upstreamTls = new(tcpClient.GetStream(), leaveInnerStreamOpen: false);
                await upstreamTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, connection.ConnectionClosed);

                await streamProxyFactory
                    .Create(new StreamDuplexPipe(clientTls), upstreamTls)
                    .ProxyAsync(connection.ConnectionClosed);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
            {
                logger.LogDebug(ex, "HTTPS interception ended for {Host}:{Port}", host, port);
            }
        }
    }

    private static SequencePosition? FindHeaderEnd(ReadOnlySequence<byte> buffer)
    {
        var bytes = buffer.ToArray();
        for (var index = 3; index < bytes.Length; index++)
        {
            if (bytes[index - 3] == '\r' && bytes[index - 2] == '\n' &&
                bytes[index - 1] == '\r' && bytes[index] == '\n')
            {
                return buffer.GetPosition(index + 1);
            }
        }

        return null;
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
