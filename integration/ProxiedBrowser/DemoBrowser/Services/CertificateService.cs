using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xilium.CefGlue;

namespace DemoBrowser.Services;

/// <summary>
/// Downloads the proxy CA certificate, holds it in memory, and decides whether a certificate
/// presented to Chromium (CEF) chains to it.
///
/// WHY in-app trust instead of the macOS keychain: installing a CA into the system or login keychain
/// requires an admin/user prompt, persists trust on the machine beyond this process, and affects every
/// other application. By validating inside <see cref="CefRequestHandler.OnCertificateError"/> with an
/// <see cref="X509Chain"/> in <see cref="X509ChainTrustMode.CustomRootTrust"/> mode, trust is scoped to
/// this process only, needs no admin rights and leaves nothing behind when the app exits.
/// </summary>
public sealed class CertificateService(ProxyDiagnostics diagnostics)
{
    private readonly ProxyDiagnostics _diagnostics = diagnostics;
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
    /// no bootstrap-trust problem (this request is made by .NET, not by the proxied browser engine), and relaxing
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
            _diagnostics.Info("CA", $"Proxy CA loaded from {caCertUrl} ({bytes.Length} bytes)", Describe(_proxyCa));
            foreach (var problem in FindRootProblems(_proxyCa))
            {
                _diagnostics.Warning("CA", problem);
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                   or CryptographicException or FormatException or ArgumentException)
        {
            var error = $"Could not load proxy CA from {caCertUrl}: {ex.Message}";
            _diagnostics.Error("CA", error, ex.ToString());
            return error;
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
    /// Handler shared by every tab for <see cref="CefRequestHandler.OnCertificateError"/>.
    /// Fully synchronous by design: the decision is made and the CEF callback answered before returning.
    ///
    /// Note: with the proxy MITM-ing traffic, essentially every HTTPS site will raise this event because
    /// the proxy re-signs each site's certificate with its own CA. Those all chain to the proxy CA and are
    /// allowed (the CEF callback is continued, i.e. the request proceeds); that is the intended design, not a
    /// bug. Anything that does not chain to the proxy CA (a genuinely bad certificate, or no CA loaded at all)
    /// is left to CEF's default handling so the normal Chromium error page appears.
    /// </summary>
    /// <returns>
    /// Whether the certificate was accepted (which is also what the CEF handler must return: <c>true</c> =
    /// "handled, callback continued", <c>false</c> = default behaviour), and the chain it presented (leaf first)
    /// for the lock popup.
    ///
    /// The chain is read here because CEF exposes no way to read the certificate of a *successful* connection
    /// (its DevTools Security domain stays silent and <c>Network.getCertificate</c> answers with an empty list),
    /// while this callback carries the full chain — and behind the MITM proxy it fires for every HTTPS site.
    ///
    /// <see cref="CefSslInfo.GetX509Certificate"/> is called exactly once: every call wraps the same native
    /// object in a new managed proxy that releases it on dispose, so two wrappers would free it twice.
    /// </returns>
    public (bool Trusted, IReadOnlyList<X509Certificate2> Chain) HandleServerCertificateError(CefSslInfo? sslInfo, CefCallback callback)
    {
        var leaf = ReadLeaf(sslInfo);
        if (leaf is null)
        {
            _diagnostics.Error("Certificate", "CEF reported a certificate error but the leaf could not be read");
            return (false, []);
        }

        var (trusted, chain) = BuildAgainstProxyCa(leaf);
        if (trusted)
        {
            callback.Continue();
        }

        return (trusted, chain);
    }

    /// <summary>
    /// Reads the presented leaf certificate out of a CEF <see cref="CefSslInfo"/>.
    ///
    /// WHY only the leaf: CefGlue's binding of CEF's issuer-chain API (<c>GetPEMEncodedIssuerChain</c>) wraps a
    /// native *array* of binary values in a single managed proxy, and releasing that proxy corrupts the native
    /// reference counts — the process then dies in a finalizer. The leaf is all that is needed: the proxy signs
    /// each site's certificate directly with its CA, and the resulting chain is rebuilt below from our own CA.
    ///
    /// <see cref="CefSslInfo.GetX509Certificate"/> is called exactly once: every call wraps the same native
    /// object in a new managed proxy that releases it on dispose, so two wrappers would free it twice.
    /// </summary>
    private static X509Certificate2? ReadLeaf(CefSslInfo? sslInfo)
    {
        var certificate = sslInfo?.GetX509Certificate();
        if (certificate is null)
        {
            return null;
        }

        try
        {
            return X509Certificate2.CreateFromPem(PemOf(certificate.GetPemEncoded()));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds <paramref name="leaf"/> against the in-memory proxy CA as the only trusted root and returns the
    /// resulting chain (leaf first, then the issuers up to the CA) for the lock popup.
    ///
    /// The chain is read here because CEF exposes no way to read the certificate of a *successful* connection
    /// (its DevTools Security domain stays silent and <c>Network.getCertificate</c> answers with an empty list),
    /// while this callback carries it — and behind the MITM proxy it fires for every HTTPS site.
    /// </summary>
    private (bool Trusted, IReadOnlyList<X509Certificate2> Chain) BuildAgainstProxyCa(X509Certificate2 leaf)
    {
        var proxyCa = _proxyCa;
        if (proxyCa is null)
        {
            _diagnostics.Error("Certificate", $"Rejected {Subject(leaf)}: no proxy CA is loaded",
                "The CA download failed or the proxy is switched off, so nothing can be trusted.");
            return (false, [leaf]);
        }

        // Trivial case: the server presented the CA itself.
        if (string.Equals(leaf.Thumbprint, proxyCa.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            _diagnostics.Info("Certificate", $"Trusted {Subject(leaf)}: the server presented the proxy CA itself");
            return (true, [leaf]);
        }

        try
        {
            using var x509Chain = new X509Chain();
            x509Chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            x509Chain.ChainPolicy.CustomTrustStore.Add(proxyCa);
            x509Chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            x509Chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreWrongUsage;

            if (!x509Chain.Build(leaf))
            {
                // The interesting failure: Chromium already said "untrusted issuer" (expected behind a MITM
                // proxy) and our own override could not repair it either. The chain status says why.
                _diagnostics.Error("Certificate", $"Rejected {Subject(leaf)}: does not chain to the proxy CA",
                    string.Join('\n',
                    [
                        "leaf issuer  : " + leaf.Issuer,
                        "proxy CA     : " + proxyCa.Subject,
                        "chain status : " + DescribeChainStatus(x509Chain),
                        "chain built  : " + DescribeElements(x509Chain),
                    ]));
                return (false, [leaf]);
            }

            // The thumbprint is logged because a MITM proxy that mints a *new* leaf per connection makes
            // Chromium loop here forever: it records the bypass for the exact certificate it saw and then
            // restarts the transaction, which meets a different one. Identical thumbprints across the
            // repeated lines mean the proxy caches per host, as it must.
            _diagnostics.Info("Certificate", $"Trusted {Subject(leaf)}: chains to the proxy CA",
                string.Join('\n',
                [
                    "chain built  : " + DescribeElements(x509Chain),
                    "leaf thumb   : " + leaf.Thumbprint,
                ]));

            // ChainElements holds the validated path (leaf → … → proxy CA); copy it, the originals are freed
            // together with the X509Chain.
            var chain = x509Chain.ChainElements
                .Select(element => X509CertificateLoader.LoadCertificate(element.Certificate.RawData))
                .ToList();
            leaf.Dispose();
            return (true, chain);
        }
        catch (CryptographicException ex)
        {
            _diagnostics.Error("Certificate", $"Rejected {Subject(leaf)}: chain building threw", ex.ToString());
            return (false, [leaf]);
        }
    }

    /// <summary>Common name (or the full subject, if there is none) of a certificate, for log lines.</summary>
    private static string Subject(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrEmpty(name) ? certificate.Subject : name;
    }

    /// <summary>The distinct chain status flags, e.g. "PartialChain, UntrustedRoot".</summary>
    private static string DescribeChainStatus(X509Chain chain)
    {
        var flags = chain.ChainStatus
            .Concat(chain.ChainElements.SelectMany(element => element.ChainElementStatus))
            .Select(status => status.Status.ToString())
            .Where(text => text != nameof(X509ChainStatusFlags.NoError))
            .Distinct()
            .ToArray();

        return flags.Length == 0 ? "no flags reported" : string.Join(", ", flags);
    }

    /// <summary>The path the chain builder actually assembled, leaf first.</summary>
    private static string DescribeElements(X509Chain chain) =>
        chain.ChainElements.Count == 0
            ? "(empty)"
            : string.Join(" -> ", chain.ChainElements.Select(element => Subject(element.Certificate)));

    /// <summary>
    /// Everything about the CA that matters when a chain refuses to build: whether it really is a CA, whether it
    /// is still valid, and its subject key identifier (which has to match the leaf's authority key identifier).
    /// </summary>
    private static string Describe(X509Certificate2 certificate)
    {
        var basicConstraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        var keyIdentifier = certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault();
        return string.Join('\n',
        [
            "subject       : " + certificate.Subject,
            "issuer        : " + certificate.Issuer,
            "thumbprint    : " + certificate.Thumbprint,
            "valid         : " + certificate.NotBefore.ToString("u") + " .. " + certificate.NotAfter.ToString("u"),
            "is CA         : " + (basicConstraints is null ? "no basicConstraints extension" : basicConstraints.CertificateAuthority.ToString()),
            "subject key id: " + (keyIdentifier?.SubjectKeyIdentifier ?? "(none)"),
            "self-signed   : " + (certificate.Subject == certificate.Issuer),
        ]);
    }

    /// <summary>Reasons this certificate cannot serve as a custom trust root; each one makes every chain fail.</summary>
    private static IEnumerable<string> FindRootProblems(X509Certificate2 certificate)
    {
        var basicConstraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (basicConstraints is null)
        {
            yield return "The CA has no basicConstraints extension, so .NET will not accept it as a trust root.";
        }
        else if (!basicConstraints.CertificateAuthority)
        {
            yield return "The CA has basicConstraints CA=false: it is an end-entity certificate, not a CA.";
        }

        var now = DateTime.Now;
        if (now < certificate.NotBefore || now > certificate.NotAfter)
        {
            yield return $"The CA is outside its validity window ({certificate.NotBefore:u} .. {certificate.NotAfter:u}).";
        }

        if (certificate.Subject != certificate.Issuer)
        {
            yield return "The CA is not self-signed: it is an intermediate, so leaves only chain if its issuer is trusted too.";
        }
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

    private static string PemOf(CefBinaryValue? value)
    {
        if (value is null)
        {
            return "";
        }

        using (value)
        {
            return value.IsValid ? Encoding.ASCII.GetString(value.ToArray()) : "";
        }
    }
}
