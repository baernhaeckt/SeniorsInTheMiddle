using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

using SeniorsInTheMiddle.Proxy.Forwarding;
using SeniorsInTheMiddle.Proxy.Telemetry;

// Aliased rather than imported: Microsoft.Net.Http.Headers also defines MediaTypeHeaderValue,
// and the content headers below are built with the System.Net.Http.Headers one.
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace Backend.Tests.Integration;

/// <summary>
/// HTTPS the client asked for, read and forwarded by the proxy.
///
/// A CONNECT used to become a byte tunnel, so everything the forwarding path does -- the
/// destination, the telemetry, the body rewrite and its framing -- reached plaintext HTTP
/// only, which is the smaller half of what this proxy exists to look at. These pin that the
/// decrypted requests now go through the same path, and that a tunnel carrying something other
/// than HTTP still gets the byte tunnel it needs.
/// </summary>
[TestClass]
public class InterceptedHttpsTests
{
    /// <summary>Rewrites every body to <paramref name="replacement"/>.</summary>
    private static IRequestBodyMutation Replacing(string replacement)
        => new DelegateMutation((_, _) => Encoding.UTF8.GetBytes(replacement));

    [TestMethod]
    public async Task Https_Request_Is_Decrypted_And_Forwarded_To_Its_Real_Destination()
    {
        const string payload = """{"customer":"Hans Muster","iban":"CH93 0076 2011 6238 5295 7"}""";

        await using TunnelHarness harness = await TunnelHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using ByteArrayContent content = new(Encoding.UTF8.GetBytes(payload));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(harness.DestinationUri, "/v1/orders?page=2"), content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        Assert.AreEqual("POST", received.Method);
        Assert.AreEqual("/v1/orders?page=2", received.Target);
        Assert.AreEqual(harness.DestinationUri.Authority, received.Host);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(payload), received.Body);
        Framing.MatchesBody(received);
        Framing.IsUnambiguous(received);
    }

    /// <summary>
    /// The client is offered a certificate this proxy minted, not the origin's. Without that
    /// the connection was never intercepted and everything else here would be passing for the
    /// wrong reason.
    /// </summary>
    [TestMethod]
    public async Task Client_Is_Offered_A_Certificate_From_The_Proxys_Own_Authority()
    {
        await using TunnelHarness harness = await TunnelHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/health"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(
            harness.PresentedIssuers.Any(issuer => issuer.Contains("SeniorsInTheMiddle", StringComparison.Ordinal)),
            $"The client was not offered an intercepted certificate. Issuers: {string.Join(" / ", harness.PresentedIssuers)}");
    }

    /// <summary>
    /// The point of the whole exercise: a body inside TLS is rewritten, and the request that
    /// leaves describes the bytes it actually carries.
    /// </summary>
    [TestMethod]
    public async Task Body_Inside_The_Tunnel_Is_Rewritten_And_Reframed()
    {
        const string replacement = """{"customer":"Anna Beispiel","iban":"CH00 0000 0000 0000 0000 0","note":"padded"}""";

        await using TunnelHarness harness = await TunnelHarness.StartAsync(Replacing(replacement));
        using HttpClient client = harness.CreateProxiedClient();

        using ByteArrayContent content = new("""{"a":1}"""u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(harness.DestinationUri, "/v1/orders"), content);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RecordedRequest received = harness.RequireReceived();
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(replacement), received.Body);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(replacement).ToString(), received.Value(HeaderNames.ContentLength));
        Assert.IsFalse(received.Has(HeaderNames.ContentEncoding), "Content-Encoding outlived the bytes it described.");
        Assert.AreEqual("application/json", received.Value(HeaderNames.ContentType));
        Framing.MatchesBody(received);
        Framing.IsUnambiguous(received);
    }

    /// <summary>One CONNECT carries many requests, and each one has to be read on its own.</summary>
    [TestMethod]
    public async Task One_Tunnel_Carries_Several_Requests()
    {
        await using TunnelHarness harness = await TunnelHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        foreach (string path in new[] { "/first", "/second", "/third" })
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, path));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(path, harness.RequireReceived().Target);
        }
    }

    /// <summary>
    /// The dashboard's traffic row for an intercepted request now names the site the client
    /// actually asked for. Before, HTTPS never reached the code that reports this at all.
    /// </summary>
    [TestMethod]
    public async Task Telemetry_Names_The_Real_Https_Destination()
    {
        await using TunnelHarness harness = await TunnelHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(harness.DestinationUri, "/v1/patients"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        RequestObserved observed = harness.Telemetry.OfType<RequestObserved>().Single();
        Assert.AreEqual(TelemetryScheme.Https, observed.Scheme);
        Assert.AreEqual("127.0.0.1", observed.Host);
        Assert.AreEqual("/v1/patients", observed.Path);
        Assert.AreEqual("GET", observed.Method);
    }

    /// <summary>
    /// A tunnel aimed at the proxy's own address is answered here rather than forwarded back
    /// to ourselves. This is what lets a device fetch the CA over HTTPS while it is already
    /// pointed at us as its proxy.
    /// </summary>
    [TestMethod]
    public async Task Tunnelled_Request_Aimed_At_The_Proxy_Is_Answered_Locally()
    {
        await using TunnelHarness harness = await TunnelHarness.StartAsync();
        using HttpClient client = harness.CreateProxiedClient();

        string body = await client.GetStringAsync(new Uri($"https://{harness.ProxyUri.Authority}/ca.crt"));

        Assert.AreEqual("certificate", body);
        Assert.IsNull(harness.Received, "A request meant for the proxy was forwarded to the origin.");
    }

    /// <summary>
    /// The two branches, told apart on identical setup by nothing but the first bytes inside
    /// the tunnel. Both target a port nothing listens on, so the only thing that differs is
    /// which code decided what to do.
    ///
    /// HTTP goes to the forwarder, which cannot reach the origin and says so in a 502 the
    /// client can read.
    /// </summary>
    [TestMethod]
    public async Task Http_Inside_A_Tunnel_Reaches_The_Forwarder()
    {
        await using TunnelHarness harness = await TunnelHarness.StartAsync();
        string authority = $"127.0.0.1:{harness.UnreachablePort}";

        (TcpClient socket, SslStream tls) = await harness.ConnectTunnelAsync(authority);
        using (socket)
        using (tls)
        {
            await tls.WriteAsync(Encoding.ASCII.GetBytes(
                $"GET /orders HTTP/1.1\r\nHost: {authority}\r\nConnection: close\r\n\r\n"));
            await tls.FlushAsync();

            byte[] buffer = new byte[256];
            int read = await tls.ReadAsync(buffer);
            string answer = Encoding.ASCII.GetString(buffer, 0, read);

            Assert.Contains("502", answer);
        }
    }

    /// <summary>
    /// Anything else gets the byte tunnel it always had. Kestrel would answer 400 to a mail or
    /// database session that used to work, which is the regression this branch exists to
    /// prevent.
    /// </summary>
    [TestMethod]
    public async Task Non_Http_Inside_A_Tunnel_Is_Never_Parsed_As_Http()
    {
        await using TunnelHarness harness = await TunnelHarness.StartAsync();
        string authority = $"127.0.0.1:{harness.UnreachablePort}";

        (TcpClient socket, SslStream tls) = await harness.ConnectTunnelAsync(authority);
        using (socket)
        using (tls)
        {
            await tls.WriteAsync(Encoding.ASCII.GetBytes("EHLO mail.example.com\r\n"));
            await tls.FlushAsync();

            byte[] buffer = new byte[256];
            int read = await tls.ReadAsync(buffer);
            string answer = Encoding.ASCII.GetString(buffer, 0, read);

            Assert.IsEmpty(answer, $"An opaque tunnel was answered with HTTP: {answer.Trim()}");
        }

        Assert.IsTrue(
            harness.Logs.Any(line => line.Contains($"Could not reach {authority}", StringComparison.Ordinal)),
            $"The tunnel did not take the opaque branch. Logs: {string.Join(" / ", harness.Logs.TakeLast(10))}");
    }

    /// <summary>An HTTP/2 preface is not HTTP/1.1 and must not be handed to a parser that only
    /// reads HTTP/1.1.</summary>
    [TestMethod]
    public async Task Http2_Preface_Is_Treated_As_Opaque()
    {
        await using TunnelHarness harness = await TunnelHarness.StartAsync();
        string authority = $"127.0.0.1:{harness.UnreachablePort}";

        (TcpClient socket, SslStream tls) = await harness.ConnectTunnelAsync(authority);
        using (socket)
        using (tls)
        {
            await tls.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"));
            await tls.FlushAsync();

            byte[] buffer = new byte[256];
            int read = await tls.ReadAsync(buffer);

            Assert.AreEqual(0, read, "An HTTP/2 preface was answered rather than tunnelled.");
        }

        Assert.IsTrue(
            harness.Logs.Any(line => line.Contains($"Could not reach {authority}", StringComparison.Ordinal)),
            $"The tunnel did not take the opaque branch. Logs: {string.Join(" / ", harness.Logs.TakeLast(10))}");
    }

    private sealed class DelegateMutation(Func<ReadOnlyMemory<byte>, RequestBodyDescriptor, byte[]?> mutate)
        : IRequestBodyMutation
    {
        public ValueTask<byte[]?> MutateAsync(
            ReadOnlyMemory<byte> body,
            RequestBodyDescriptor descriptor,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(mutate(body, descriptor));
    }
}
