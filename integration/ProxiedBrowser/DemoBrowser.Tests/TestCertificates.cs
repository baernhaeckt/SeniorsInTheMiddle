using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DemoBrowser.Tests;

/// <summary>
/// Builds throw-away certificate authorities and leaf certificates in memory, so the trust logic can be tested
/// without a proxy — the same shapes the MITM proxy produces: a self-signed CA that re-signs each site.
/// </summary>
internal static class TestCertificates
{
    public static X509Certificate2 CreateCa(
        string commonName = "Test Proxy CA",
        bool certificateAuthority = true,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, false));

        return request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>A site certificate signed by <paramref name="issuer"/> — what the proxy hands to the browser.</summary>
    public static X509Certificate2 CreateLeaf(string host, X509Certificate2 issuer)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddDnsName(host);
        request.CertificateExtensions.Add(alternativeNames.Build());

        var serialNumber = new byte[8];
        RandomNumberGenerator.Fill(serialNumber);

        // A leaf may never outlive its issuer, and the expired-CA test signs with a CA that is already past its
        // NotAfter — so the leaf simply borrows the issuer's validity window, which is valid in every case.
        return request.Create(issuer, issuer.NotBefore, issuer.NotAfter, serialNumber);
    }

    public static byte[] ToDer(this X509Certificate2 certificate) => certificate.Export(X509ContentType.Cert);

    public static byte[] ToPem(this X509Certificate2 certificate) =>
        Encoding.ASCII.GetBytes(certificate.ExportCertificatePem() + "\n");
}

/// <summary>
/// A one-shot HTTP server on 127.0.0.1 that serves a fixed body, standing in for the proxy's plain-HTTP port
/// where the CA is published. A raw socket rather than <see cref="HttpListener"/>, which needs a URL ACL.
/// </summary>
internal sealed class StubHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();

    private StubHttpServer(byte[] body, string contentType, int statusCode)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/ca.cer";
        _ = ServeAsync(body, contentType, statusCode);
    }

    public string Url { get; }

    public static StubHttpServer Serving(byte[] body, string contentType = "application/x-x509-ca-cert") =>
        new(body, contentType, 200);

    public static StubHttpServer Failing(int statusCode) =>
        new([], "text/plain", statusCode);

    private async Task ServeAsync(byte[] body, string contentType, int statusCode)
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                using var stream = client.GetStream();

                // Read (and discard) the request line and headers; the body of a GET is empty. One read is
                // enough for a request this small, and a short read only means fewer headers to ignore.
                var request = new byte[8192];
                _ = await stream.ReadAsync(request, _shutdown.Token);

                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {statusCode} \r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, _shutdown.Token);
                await stream.WriteAsync(body, _shutdown.Token);
                await stream.FlushAsync(_shutdown.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
        {
            // Shutting down, or the client hung up: both are normal for a stub.
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Stop();
        _shutdown.Dispose();
    }
}
