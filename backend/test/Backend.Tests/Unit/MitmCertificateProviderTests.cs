using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// The certificates the proxy presents to an intercepted client.
///
/// The provider signs every one of them with a single private key it keeps for the process,
/// because minting a key per host puts 50-150 ms of RSA generation on the connection whose
/// handshake asked for it. These tests pin the two things that arrangement has to keep true:
/// the key survives being handed to one certificate after another, and a host still gets the
/// same certificate every time it is asked for.
/// </summary>
[TestClass]
public class MitmCertificateProviderTests
{
    private string _caPath = string.Empty;

    [TestInitialize]
    public void CreateCaPath()
        => _caPath = Path.Combine(Path.GetTempPath(), $"sitm-ca-{Guid.NewGuid():N}.pfx");

    [TestCleanup]
    public void RemoveCaFiles()
    {
        File.Delete(_caPath);
        File.Delete(Path.ChangeExtension(_caPath, ".cer"));
    }

    private MitmCertificateProvider Provider()
        => new(
            new ConfigurationBuilder()
                .AddInMemoryCollection([new KeyValuePair<string, string?>("Mitm:CertificatePath", _caPath)])
                .Build(),
            NullLogger<MitmCertificateProvider>.Instance);

    /// <summary>
    /// The regression the shared key could introduce: a certificate that takes the key and
    /// then leaves it unusable would make the second intercepted host fail its handshake, and
    /// only the second -- the first would look perfectly fine.
    /// </summary>
    [TestMethod]
    public void Certificates_For_Several_Hosts_All_Carry_A_Usable_Private_Key()
    {
        MitmCertificateProvider provider = Provider();

        foreach (string host in new[] { "example.com", "api.example.org", "127.0.0.1" })
        {
            X509Certificate2 certificate = provider.GetServerCertificate(host);

            Assert.IsTrue(certificate.HasPrivateKey, $"No private key on the certificate for {host}.");

            // Held rather than assumed: a key that cannot sign is a handshake that fails.
            using RSA key = certificate.GetRSAPrivateKey()
                ?? throw new InvalidOperationException($"No RSA private key for {host}.");

            byte[] signature = key.SignData("handshake"u8.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            Assert.IsTrue(key.VerifyData(
                "handshake"u8.ToArray(),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));
        }
    }

    /// <summary>
    /// A client that was told to accept an untrusted certificate remembers the exact one it
    /// saw and then retries. Handing it a different certificate on the retry restarts the
    /// prompt, forever, and the page never loads.
    /// </summary>
    [TestMethod]
    public void The_Same_Host_Gets_The_Same_Certificate_Every_Time()
    {
        MitmCertificateProvider provider = Provider();

        Assert.AreEqual(
            provider.GetServerCertificate("example.com").Thumbprint,
            provider.GetServerCertificate("example.com").Thumbprint);
    }

    [TestMethod]
    public void Different_Hosts_Get_Different_Certificates()
    {
        MitmCertificateProvider provider = Provider();

        Assert.AreNotEqual(
            provider.GetServerCertificate("example.com").Thumbprint,
            provider.GetServerCertificate("other.example.com").Thumbprint);
    }
}
