using System.Security.Cryptography.X509Certificates;
using DemoBrowser.Services;
using Xunit;

namespace DemoBrowser.Tests;

/// <summary>
/// Covers the decision that makes or breaks browsing behind the MITM proxy: does the certificate the proxy
/// presents chain to the CA we downloaded? Chromium always rejects those certificates itself — the app is
/// expected to override that — so a bug here shows up only as a permanently blank page.
/// </summary>
public class ProxyCaTests
{
    private static (CertificateService Service, ProxyDiagnostics Diagnostics) NewService()
    {
        var diagnostics = new ProxyDiagnostics();
        return (new CertificateService(diagnostics), diagnostics);
    }

    private static async Task<CertificateService> WithCaAsync(X509Certificate2 ca, bool pem = false)
    {
        var (service, _) = NewService();
        using var server = StubHttpServer.Serving(pem ? ca.ToPem() : ca.ToDer());
        Assert.Null(await service.DownloadAsync(server.Url));
        return service;
    }

    [Fact]
    public async Task DownloadAsync_AcceptsDerEncodedCertificate()
    {
        using var ca = TestCertificates.CreateCa();
        var service = await WithCaAsync(ca);

        Assert.True(service.HasProxyCa);
        Assert.Equal(ca.Thumbprint, service.ProxyCa!.Thumbprint);
    }

    [Fact]
    public async Task DownloadAsync_AcceptsPemEncodedCertificate()
    {
        using var ca = TestCertificates.CreateCa();
        var service = await WithCaAsync(ca, pem: true);

        Assert.Equal(ca.Thumbprint, service.ProxyCa!.Thumbprint);
    }

    [Fact]
    public async Task DownloadAsync_ReportsHttpFailureAndKeepsNoCa()
    {
        var (service, diagnostics) = NewService();
        using var server = StubHttpServer.Failing(404);

        var error = await service.DownloadAsync(server.Url);

        Assert.NotNull(error);
        Assert.False(service.HasProxyCa);
        Assert.Contains(diagnostics.Snapshot(), e => e.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task DownloadAsync_ReportsGarbageResponse()
    {
        var (service, _) = NewService();
        using var server = StubHttpServer.Serving([1, 2, 3, 4, 5]);

        Assert.NotNull(await service.DownloadAsync(server.Url));
        Assert.False(service.HasProxyCa);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/ca.cer")]
    public async Task DownloadAsync_RejectsNonHttpUrls(string url)
    {
        var (service, _) = NewService();

        Assert.NotNull(await service.DownloadAsync(url));
    }

    // ---------------------------------------------------------------- trust

    [Fact]
    public async Task LeafSignedByTheProxyCa_IsTrusted()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.CreateLeaf("example.com", ca);
        var service = await WithCaAsync(ca);

        Assert.True(service.ChainsToProxyCa([leaf]));
    }

    [Fact]
    public async Task LeafSignedBySomeoneElse_IsRejected()
    {
        using var proxyCa = TestCertificates.CreateCa("Proxy CA");
        using var otherCa = TestCertificates.CreateCa("Unrelated CA");
        using var leaf = TestCertificates.CreateLeaf("example.com", otherCa);
        var service = await WithCaAsync(proxyCa);

        Assert.False(service.ChainsToProxyCa([leaf]));
    }

    [Fact]
    public void WithoutADownloadedCa_NothingIsTrusted()
    {
        using var ca = TestCertificates.CreateCa();
        using var leaf = TestCertificates.CreateLeaf("example.com", ca);
        var (service, _) = NewService();

        Assert.False(service.HasProxyCa);
        Assert.False(service.ChainsToProxyCa([leaf]));
    }

    [Fact]
    public async Task TheCaItself_IsTrusted()
    {
        using var ca = TestCertificates.CreateCa();
        var service = await WithCaAsync(ca);

        Assert.True(service.ChainsToProxyCa([service.ProxyCa!]));
    }

    [Fact]
    public async Task AnEmptyChain_IsRejected()
    {
        using var ca = TestCertificates.CreateCa();
        var service = await WithCaAsync(ca);

        Assert.False(service.ChainsToProxyCa([]));
    }

    [Fact]
    public async Task AnExpiredProxyCa_TrustsNothing()
    {
        using var ca = TestCertificates.CreateCa(
            notBefore: DateTimeOffset.UtcNow.AddYears(-2),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        using var leaf = TestCertificates.CreateLeaf("example.com", ca);
        var service = await WithCaAsync(ca);

        Assert.False(service.ChainsToProxyCa([leaf]));
    }

    // ------------------------------------------------------------ diagnostics

    [Fact]
    public async Task LoadingTheCa_RecordsItsDetailsForTheDiagnosticsWindow()
    {
        using var ca = TestCertificates.CreateCa("Bern Proxy CA");
        var (service, diagnostics) = NewService();
        using var server = StubHttpServer.Serving(ca.ToDer());

        await service.DownloadAsync(server.Url);

        var entry = Assert.Single(diagnostics.Snapshot(), e => e.Category == "CA" && e.Severity == DiagnosticSeverity.Info);
        Assert.Contains("Bern Proxy CA", entry.Detail);
        Assert.Contains(ca.Thumbprint, entry.Detail);
        Assert.Contains("is CA         : True", entry.Detail);
    }

    /// <summary>
    /// A proxy that publishes its *server* certificate instead of its CA is a real configuration mistake, and it
    /// looks exactly like a working setup until the first HTTPS page stays blank. The warning names it.
    /// </summary>
    [Fact]
    public async Task ACertificateThatIsNotACa_IsFlaggedOnLoad()
    {
        using var notACa = TestCertificates.CreateCa("Proxy Server", certificateAuthority: false);
        var (service, diagnostics) = NewService();
        using var server = StubHttpServer.Serving(notACa.ToDer());

        await service.DownloadAsync(server.Url);

        Assert.Contains(diagnostics.Snapshot(),
            e => e.Severity == DiagnosticSeverity.Warning && e.Message.Contains("CA=false"));
    }

    [Fact]
    public async Task AnExpiredCa_IsFlaggedOnLoad()
    {
        using var ca = TestCertificates.CreateCa(
            notBefore: DateTimeOffset.UtcNow.AddYears(-2),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        var (service, diagnostics) = NewService();
        using var server = StubHttpServer.Serving(ca.ToDer());

        await service.DownloadAsync(server.Url);

        Assert.Contains(diagnostics.Snapshot(),
            e => e.Severity == DiagnosticSeverity.Warning && e.Message.Contains("validity window"));
    }

    [Fact]
    public void Diagnostics_KeepEntriesInOrderAndClear()
    {
        var diagnostics = new ProxyDiagnostics();
        diagnostics.Info("Proxy", "first");
        diagnostics.Warning("CA", "second");
        diagnostics.Error("Certificate", "third");

        Assert.Equal(["first", "second", "third"], diagnostics.Snapshot().Select(e => e.Message));
        Assert.Contains("third", diagnostics.ToPlainText());

        diagnostics.Clear();
        Assert.Empty(diagnostics.Snapshot());
    }
}
