using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Yarp.ReverseProxy.Forwarder;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(8080, listen =>
                listen.Use((connection, next) => HandleConnection(connection, () => next())));
        });

        builder.Services.AddHttpForwarder();
        builder.Services.AddSingleton<ForwardProxy>();

        var app = builder.Build();

        app.MapMethods("/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE"],
            (ForwardProxy proxy, HttpContext context) => proxy.HandleAsync(context));

        app.Run();

        static async Task HandleConnection(ConnectionContext connection, Func<Task> next)
        {
            PipeReader input = connection.Transport.Input;
            PipeWriter output = connection.Transport.Output;
            ReadResult readResult = await input.ReadAsync(connection.ConnectionClosed);
            ReadOnlySequence<byte> buffer = readResult.Buffer;
            SequencePosition? headerEnd = FindHeaderEnd(buffer);

            if (headerEnd is null || buffer.Length == 0)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                await next();
                return;
            }

            var headerText = Encoding.ASCII.GetString(buffer.Slice(0, headerEnd.Value).ToArray());
            var requestLine = headerText.Split("\r\n", 2, StringSplitOptions.None)[0];
            if (!requestLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
            {
                input.AdvanceTo(buffer.Start, buffer.Start);
                await next();
                return;
            }

            if (!TryParseConnectTarget(requestLine, out var host, out var port))
            {
                await WriteProxyErrorAsync(output, "400 Bad Request");
                input.AdvanceTo(buffer.End);
                return;
            }

            using var tcpClient = new TcpClient();
            try
            {
                await tcpClient.ConnectAsync(host, port, connection.ConnectionClosed);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
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

            await using NetworkStream destination = tcpClient.GetStream();
            await using Stream clientInput = input.AsStream(leaveOpen: true);
            await using Stream clientOutput = output.AsStream(leaveOpen: true);
            using CancellationTokenSource tunnelCancellation = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);

            Task clientToDestination = clientInput.CopyToAsync(destination, tunnelCancellation.Token);
            Task destinationToClient = destination.CopyToAsync(clientOutput, tunnelCancellation.Token);
            await Task.WhenAny(clientToDestination, destinationToClient);
            tunnelCancellation.Cancel();

            try
            {
                await Task.WhenAll(clientToDestination, destinationToClient);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
            }
        }

        static SequencePosition? FindHeaderEnd(ReadOnlySequence<byte> buffer)
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

        static bool TryParseConnectTarget(string requestLine, out string host, out int port)
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

        static Task WriteProxyErrorAsync(PipeWriter output, string status)
            => output.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")).AsTask();
    }
}
