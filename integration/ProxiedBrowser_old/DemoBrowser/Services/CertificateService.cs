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
    /// WHY normal TLS validation: when the CA is served over https (e.g. an Azure endpoint with a
    /// publicly-trusted certificate) the default <see cref="HttpClient"/> validation is exactly right. There is
    /// no bootstrap-trust problem (this request is made by .NET, not by the proxied WebView2), and relaxing
    /// validation here would let anyone on the path substitute their own CA, which would be a security regression.
    /// The proxy also publishes the CA on its plain-HTTP port (http://host:3128/ca.cer); that is accepted too,
    /// with the obvious caveat that plain HTTP offers no protection against substitution on the path.
    /// </summary>
    public async Task<string?> DownloadAsync(string caCertUrl, CancellationToken cancellationToken = default)
    {
        _proxyCa = null;

        if (!Uri.TryCreate(caCertUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return $"CaCertUrl '{caCertUrl}' is not a valid http(s):// URL.";
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
    /// <returns><c>true</c> if the certificate was accepted because it chains to the proxy CA.</returns>
    public bool HandleServerCertificateError(CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        var trusted = IsIssuedByProxyCa(e.ServerCertificate);
        e.Action = trusted
            ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
            : CoreWebView2ServerCertificateErrorAction.Default;
        return trusted;
    }

    /// <summary>Checks whether an arbitrary chain (leaf first) ends at the in-memory proxy CA.</summary>
    public bool ChainsToProxyCa(IReadOnlyList<X509Certificate2> chain)
    {
        var proxyCa = _proxyCa;
        if (proxyCa is null || chain.Count == 0)
        {
            return false;
        }

        if (chain.Any(c => string.Equals(c.Thumbprint, proxyCa.Thumbprint, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            using var x509Chain = new X509Chain();
            x509Chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            x509Chain.ChainPolicy.CustomTrustStore.Add(proxyCa);
            x509Chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            x509Chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreWrongUsage;
            foreach (var issuer in chain.Skip(1))
            {
                x509Chain.ChainPolicy.ExtraStore.Add(issuer);
            }

            return x509Chain.Build(chain[0]);
        }
        catch (CryptographicException)
        {
            return false;
        }
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
