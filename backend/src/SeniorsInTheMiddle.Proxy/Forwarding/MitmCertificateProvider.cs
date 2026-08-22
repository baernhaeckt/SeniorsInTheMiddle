using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

sealed class MitmCertificateProvider
{
    private static readonly TimeSpan InterceptionLifetime = TimeSpan.FromDays(7);

    /// <summary>Renew a cached certificate this long before it actually expires.</summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromHours(1);

    private const int ServerKeyBits = 2048;

    private readonly X509Certificate2 _certificateAuthority;

    /// <summary>
    /// The private key every server certificate this provider mints is built on.
    ///
    /// One key for all of them rather than one per host. Two reasons, and the first one is not
    /// an optimisation.
    ///
    /// A browser cannot be asked about a bad certificate on a *subresource*. Chromium offers the
    /// embedder a certificate error for a main-frame navigation only; for a script, an API call
    /// or an image on another origin it denies the request outright, on the grounds that a user
    /// has no context to judge it. A client behind this proxy therefore cannot decide per host --
    /// it has to be told, before it starts, which key is ours, and that is only possible if there
    /// is one such key. With a key per host, a page whose subresources live on other origins (a
    /// CDN, an API host, an analytics domain) loses every one of them to
    /// ERR_CERT_AUTHORITY_INVALID while the document itself loads: a site that looks broken
    /// rather than a certificate that looks wrong. Every interception proxy shares the key for
    /// this reason (mitmproxy, Burp, Fiddler).
    ///
    /// It is also faster. Generating an RSA key costs 50-150 ms of CPU, and
    /// <see cref="GetServerCertificate"/> runs on the connection whose handshake triggered it:
    /// minting a key per host stalls the first connection to every new site, and a page pulling
    /// from dozens of hosts serializes those stalls on the accept path. Signing on its own is
    /// sub-millisecond, so with the key already made a cold host costs nothing worth measuring.
    ///
    /// Sharing it gives an attacker nothing: every one of these certificates is signed by the
    /// CA whose own private key lives in this same process and on disk beside it, so anything
    /// able to read this key could read that one and mint certificates for any host at all.
    /// </summary>
    private readonly Lazy<RSA> _serverKey = new(
        () =>
        {
            RSA key = RSA.Create(ServerKeyBits);

            // RSA.Create defers generation to the key's first use. Left to that, it would run
            // on whichever connection got there first -- and on several at once, which the key
            // object makes no promise about. Forcing it here keeps it inside the Lazy's lock.
            key.ExportParameters(includePrivateParameters: false);

            return key;
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// One certificate per intercepted host, reused for every connection to it.
    ///
    /// WHY it must be cached rather than minted per connection: a client that is told to accept an
    /// otherwise untrusted certificate records that decision for the exact certificate it saw, and then
    /// retries the connection. Chromium does this in its SSLHostStateDelegate, keyed by
    /// (host, certificate, error). Handing out a freshly generated certificate every time means the
    /// retry meets a certificate the client has never allowed, so the handshake fails again, the client
    /// asks again, allows again, retries again -- an endless loop in which the page never loads.
    /// The same applies to any client that pins or caches by fingerprint.
    ///
    /// <see cref="Lazy{T}"/> with <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> so that N
    /// concurrent connections to one host generate one key, not N.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<X509Certificate2>> _hostCertificates =
        new(StringComparer.OrdinalIgnoreCase);

    public string PublicCertificatePath { get; }

    /// <summary>
    /// DER bytes of the CA's public certificate. Handed to clients over /ca.crt so a
    /// device can trust both the intercepted sites and this app's own HTTPS endpoint.
    /// </summary>
    public byte[] PublicCertificate { get; }

    public MitmCertificateProvider(IConfiguration configuration, ILogger<MitmCertificateProvider> logger)
    {
        string path = GetPath(Setting(configuration, "Mitm:CertificatePath") ?? "mitm-ca.pfx");
        string password = Setting(configuration, "Mitm:CertificatePassword") ?? string.Empty;
        string publicPath = GetPath(
            Setting(configuration, "Mitm:CertificatePublicPath") ?? Path.ChangeExtension(path, ".cer"));
        PublicCertificatePath = publicPath;

        if (File.Exists(path))
        {
            _certificateAuthority = X509CertificateLoader.LoadPkcs12FromFile(
                path,
                password,
                X509KeyStorageFlags.Exportable);
            PublicCertificate = _certificateAuthority.Export(X509ContentType.Cert);
            WritePublicCertificate(publicPath);
            logger.LogInformation("Using MITM CA certificate {CertificatePath}; public certificate: {PublicCertificatePath}", path, publicPath);
        }
        else
        {
            using RSA key = RSA.Create(4096);
            CertificateRequest request = new(
                "CN=SeniorsInTheMiddle Proxy CA",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));

            using X509Certificate2 generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddYears(10));
            byte[] pfx = generated.Export(X509ContentType.Pfx, password);
            EnsureParentDirectory(path);
            File.WriteAllBytes(path, pfx);
            _certificateAuthority = X509CertificateLoader.LoadPkcs12(
                pfx,
                password,
                X509KeyStorageFlags.Exportable);
            PublicCertificate = _certificateAuthority.Export(X509ContentType.Cert);
            WritePublicCertificate(publicPath);
            logger.LogWarning(
                "Generated a new MITM CA at {Path}. Every client has to trust {PublicPath} "
                + "again, which is an operating-system step rather than a browser click. Mount "
                + "a volume at that directory, or set Mitm:CertificatePath, to keep one CA "
                + "across restarts.",
                path,
                publicPath);
        }
    }

    private void WritePublicCertificate(string path)
    {
        EnsureParentDirectory(path);
        if (!File.Exists(path))
            File.WriteAllBytes(path, PublicCertificate);
    }

    /// <summary>A blank setting counts as absent, so an empty env var falls back to the default.</summary>
    private static string? Setting(IConfiguration configuration, string key)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string GetPath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// The certificate for one intercepted host: generated on first use and reused afterwards, so every
    /// connection to that host presents the same certificate (see <see cref="_hostCertificates"/>).
    ///
    /// The provider owns the returned instance for the lifetime of the process; callers must not dispose it.
    /// </summary>
    public X509Certificate2 GetServerCertificate(string host)
    {
        while (true)
        {
            Lazy<X509Certificate2> entry = _hostCertificates.GetOrAdd(
                host,
                name => new Lazy<X509Certificate2>(
                    () => CreateServerCertificate([name], InterceptionLifetime),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            X509Certificate2 certificate = entry.Value;
            if (DateTime.Now < certificate.NotAfter - RenewBefore)
                return certificate;

            // Expiring: drop it and mint a new one. Connections still using the old instance keep it alive;
            // only the dictionary reference goes, so it is not disposed underneath them.
            _hostCertificates.TryRemove(new KeyValuePair<string, Lazy<X509Certificate2>>(host, entry));
        }
    }

    /// <summary>
    /// A certificate covering every name a client might use to reach us. Used both for
    /// interception and for this app's own HTTPS listener, so one trusted CA covers both.
    /// </summary>
    public X509Certificate2 CreateServerCertificate(IReadOnlyCollection<string> hosts, TimeSpan lifetime)
    {
        if (hosts.Count == 0)
            throw new ArgumentException("At least one host name is required.", nameof(hosts));

        // Not disposed here: the provider owns it and every certificate it mints shares it.
        RSA key = _serverKey.Value;
        CertificateRequest request = new(
            $"CN={hosts.First()}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder names = new();
        foreach (string host in hosts)
        {
            if (IPAddress.TryParse(host, out IPAddress? address))
                names.AddIpAddress(address);
            else
                names.AddDnsName(host);
        }

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            false));

        using X509Certificate2 signed = request.Create(
            _certificateAuthority,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.Add(lifetime),
            RandomNumberGenerator.GetBytes(16));
        using X509Certificate2 withPrivateKey = signed.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(
            withPrivateKey.Export(X509ContentType.Pfx),
            string.Empty,
            X509KeyStorageFlags.Exportable);
    }
}
