using System.Buffers;
using System.IO.Pipelines;
using System.Net;
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

        var app = builder.Build();

        using var httpClient = new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        });

        var requestConfig = new ForwarderRequestConfig
        {
            ActivityTimeout = TimeSpan.FromMinutes(2)
        };

        app.MapMethods("/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE"],
            (HttpContext context, IHttpForwarder forwarder) =>
                ForwardHttpRequestAsync(context, forwarder, httpClient, requestConfig));

        app.Run();

        static async Task ForwardHttpRequestAsync(
            HttpContext context,
            IHttpForwarder forwarder,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig)
        {
            var destination = GetDestinationUri(context);
            if (destination is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("The proxy request must contain a valid destination URI.");
                return;
            }

            var error = await forwarder.SendAsync(
                context,
                destination.GetLeftPart(UriPartial.Authority),
                httpClient,
                requestConfig,
                new ForwardProxyTransformer(destination));

            if (error != ForwarderError.None && !context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }

        static async Task HandleConnection(ConnectionContext connection, Func<Task> next)
        {
            var input = connection.Transport.Input;
            var output = connection.Transport.Output;
            var readResult = await input.ReadAsync(connection.ConnectionClosed);
            var buffer = readResult.Buffer;
            var headerEnd = FindHeaderEnd(buffer);

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

            await using var destination = tcpClient.GetStream();
            await using var clientInput = input.AsStream(leaveOpen: true);
            await using var clientOutput = output.AsStream(leaveOpen: true);
            using var tunnelCancellation = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);

            var clientToDestination = clientInput.CopyToAsync(destination, tunnelCancellation.Token);
            var destinationToClient = destination.CopyToAsync(clientOutput, tunnelCancellation.Token);
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

        static Uri? GetDestinationUri(HttpContext context)
        {
            var rawTarget = context.Request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
            if (Uri.TryCreate(rawTarget, UriKind.Absolute, out var absoluteUri) &&
                (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps))
            {
                return absoluteUri;
            }

            if (!Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}",
                    UriKind.Absolute, out var requestUri) ||
                requestUri.Scheme != Uri.UriSchemeHttp && requestUri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            return requestUri;
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