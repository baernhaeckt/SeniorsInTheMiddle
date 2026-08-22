using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace DemoBrowser.Services;

/// <summary>
/// Downloads the proxy CA certificate, holds it in memory, and decides whether a certificate
/// presented to WebView2 chains to it.
///
/// WHY in-app trust instead of the Windows certificate store: installing a CA into the machine or user
/// store requires elevated rights (or at least a user prompt), persists trust on the machine beyond this
/// process, and affects every other application. By validating inside
/// <see cref="CoreWebView2.ServerCertificateErrorDetected"/> with an <see cref="X509Chain"/> in
/// <see cref="X509ChainTrustMode.CustomRootTrust"/> mode, trust is scoped to this process only, needs no
/// admin rights and leaves nothing behind when the app exits.
/// </summary>
public sealed class CertificateService
{
    private X509Certificate2? _proxyCa;

    /// <summary>The in-memory proxy CA, or <c>null</c> if the download/parse failed.</summary>
    public X509Certificate2? ProxyCa => _proxyCa;

    public bool HasProxyCa => _proxyCa is not null;

    /// <summary>
    /// Downloads and parses the CA certificate from <paramref name="caCertUrl"/>.
    /// Returns <c>null</c> on success, otherwise a human-readable error message.
    ///
    /// WHY normal TLS validation: the CA is served by an Azure Web App with a valid, publicly-trusted
    /// certificate, so the default <see cref="HttpClient"/> validation is exactly right. There is no
    /// bootstrap-trust problem (this request is made by .NET, not by the proxied WebView2), and relaxing
    /// validation here would let anyone on the path substitute their own CA, which would be a security regression.
    /// </summary>
    public async Task<string?> DownloadAsync(string caCertUrl, CancellationToken cancellationToken = default)
    {
        _proxyCa = null;

        if (!Uri.TryCreate(caCertUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return $"CaCertUrl '{caCertUrl}' is not a valid https:// URL.";
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            _proxyCa = Parse(bytes);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                   or CryptographicException or FormatException or ArgumentException)
        {
            return $"Could not load proxy CA from {caCertUrl}: {ex.Message}";
        }
    }

    /// <summary>Parses PEM ("-----BEGIN CERTIFICATE-----") or DER bytes using the non-obsolete .NET 10 loaders.</summary>
    private static X509Certificate2 Parse(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            return X509Certificate2.CreateFromPem(text);
        }

        return X509CertificateLoader.LoadCertificate(bytes);
    }

    /// <summary>
    /// Handler shared by every tab for <see cref="CoreWebView2.ServerCertificateErrorDetected"/>.
    /// Fully synchronous by design: no deferral, no awaits.
    ///
    /// Note: with the proxy MITM-ing traffic, essentially every HTTPS site will raise this event because
    /// the proxy re-signs each site's certificate with its own CA. Those all chain to the proxy CA and are
    /// allowed; that is the intended design, not a bug. Anything that does not chain to the proxy CA
    /// (a genuinely bad certificate, or no CA loaded at all) falls through to
    /// <see cref="CoreWebView2ServerCertificateErrorAction.Default"/> so the normal Edge interstitial appears.
    /// </summary>
    public void HandleServerCertificateError(CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        e.Action = IsIssuedByProxyCa(e.ServerCertificate)
            ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
            : CoreWebView2ServerCertificateErrorAction.Default;
    }

    private bool IsIssuedByProxyCa(CoreWebView2Certificate? certificate)
    {
        var proxyCa = _proxyCa;
        if (proxyCa is null || certificate is null)
        {
            return false;
        }

        var issuers = new List<X509Certificate2>();
        try
        {
            using var leaf = X509Certificate2.CreateFromPem(certificate.ToPemEncoding());

            // Trivial case: the server presented the CA itself.
            if (string.Equals(leaf.Thumbprint, proxyCa.Thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(proxyCa);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreWrongUsage;

            foreach (var pem in certificate.PemEncodedIssuerCertificateChain ?? [])
            {
                if (string.IsNullOrWhiteSpace(pem))
                {
                    continue;
                }

                var issuer = X509Certificate2.CreateFromPem(pem);
                issuers.Add(issuer);
                chain.ChainPolicy.ExtraStore.Add(issuer);
            }

            return chain.Build(leaf);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
        finally
        {
            foreach (var issuer in issuers)
            {
                issuer.Dispose();
            }
        }
    }
}
