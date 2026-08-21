using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

sealed class MitmCertificateProvider
{
    private readonly X509Certificate2 _certificateAuthority;

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

    /// <summary>A short-lived certificate for one intercepted host.</summary>
    public X509Certificate2 CreateServerCertificate(string host)
        => CreateServerCertificate([host], TimeSpan.FromDays(7));

    /// <summary>
    /// A certificate covering every name a client might use to reach us. Used both for
    /// interception and for this app's own HTTPS listener, so one trusted CA covers both.
    /// </summary>
    public X509Certificate2 CreateServerCertificate(IReadOnlyCollection<string> hosts, TimeSpan lifetime)
    {
        if (hosts.Count == 0)
            throw new ArgumentException("At least one host name is required.", nameof(hosts));

        using RSA key = RSA.Create(2048);
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
