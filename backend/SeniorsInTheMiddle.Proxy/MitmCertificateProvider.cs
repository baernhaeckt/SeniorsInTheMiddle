using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

sealed class MitmCertificateProvider
{
    private readonly X509Certificate2 _certificateAuthority;

    public string PublicCertificatePath { get; }

    public MitmCertificateProvider(IConfiguration configuration, ILogger<MitmCertificateProvider> logger)
    {
        string path = GetPath(configuration["Mitm:CertificatePath"] ?? "mitm-ca.pfx");
        string password = configuration["Mitm:CertificatePassword"] ?? string.Empty;
        string publicPath = GetPath(
            configuration["Mitm:CertificatePublicPath"] ?? Path.ChangeExtension(path, ".cer"));
        PublicCertificatePath = publicPath;

        if (File.Exists(path))
        {
            _certificateAuthority = X509CertificateLoader.LoadPkcs12FromFile(
                path,
                password,
                X509KeyStorageFlags.Exportable);
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
            WritePublicCertificate(publicPath);
            logger.LogWarning("Generated MITM CA certificate at {Path}. Trust {PublicPath} on clients before using HTTPS interception.", path, publicPath);
        }
    }

    private void WritePublicCertificate(string path)
    {
        EnsureParentDirectory(path);
        if (!File.Exists(path))
            File.WriteAllBytes(path, _certificateAuthority.Export(X509ContentType.Cert));
    }

    private static string GetPath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    public X509Certificate2 CreateServerCertificate(string host)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            $"CN={host}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName(host);
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
            DateTimeOffset.UtcNow.AddDays(7),
            RandomNumberGenerator.GetBytes(16));
        using X509Certificate2 withPrivateKey = signed.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(
            withPrivateKey.Export(X509ContentType.Pfx),
            string.Empty,
            X509KeyStorageFlags.Exportable);
    }
}
