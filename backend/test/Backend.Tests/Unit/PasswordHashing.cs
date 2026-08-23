using SeniorsInTheMiddle.Proxy.Auth.Security;

using System.Security.Cryptography;
using System.Text;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins the hashing contract: shape of hash and salt, that equal passwords with different
/// salts differ, and that the input is required to be base64.
/// </summary>
[TestClass]
public class PasswordHashingTests
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [TestMethod]
    public void Hash_Returns_32Byte_Hash_And_16Byte_Salt()
    {
        var passwordB64 = B64("P@ssw0rd!");
        var (hash, salt) = PasswordHashing.Hash(passwordB64);

        Assert.IsNotNull(hash);
        Assert.IsNotNull(salt);
        Assert.HasCount(32, hash);
        Assert.HasCount(16, salt);
    }

    [TestMethod]
    public void Verify_True_For_Correct_Password()
    {
        var passwordB64 = B64("Correct#1");
        var (hash, salt) = PasswordHashing.Hash(passwordB64);

        var ok = PasswordHashing.Verify(
            passwordB64,
            Convert.ToBase64String(hash),
            Convert.ToBase64String(salt));

        Assert.IsTrue(ok);
    }

    [TestMethod]
    public void Verify_False_For_Wrong_Password()
    {
        var passwordB64 = B64("Correct#1");
        var wrongPasswordB64 = B64("Wrong#1");
        var (hash, salt) = PasswordHashing.Hash(passwordB64);

        var ok = PasswordHashing.Verify(
            wrongPasswordB64,
            Convert.ToBase64String(hash),
            Convert.ToBase64String(salt));

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void Hash_Uses_Random_Salt_So_Hashes_Differ()
    {
        var passwordB64 = B64("SamePassword");
        var (hash1, salt1) = PasswordHashing.Hash(passwordB64);
        var (hash2, salt2) = PasswordHashing.Hash(passwordB64);

        CollectionAssert.AreNotEqual(salt1, salt2);
        CollectionAssert.AreNotEqual(hash1, hash2);
    }

    [TestMethod]
    public void Deterministic_For_Same_Salt()
    {
        var passwordB64 = B64("Deterministic!");
        var (_, salt) = PasswordHashing.Hash(passwordB64);

        // Recompute using same salt
        var hashA = InvokePrivateHash(passwordB64, salt);
        var hashB = InvokePrivateHash(passwordB64, salt);

        CollectionAssert.AreEqual(hashA, hashB);
        Assert.IsTrue(PasswordHashing.Verify(
            passwordB64,
            Convert.ToBase64String(hashA),
            Convert.ToBase64String(salt)));
    }

    [TestMethod]
    public void Hash_Throws_For_Plaintext_Password_Not_Base64()
    {
        Assert.ThrowsExactly<FormatException>(() => PasswordHashing.Hash("not-base64"));
    }

    [TestMethod]
    public void Verify_Throws_For_Invalid_Base64_Hash()
    {
        var passwordB64 = B64("X");
        var (_, salt) = PasswordHashing.Hash(passwordB64);

        Assert.ThrowsExactly<FormatException>(() => PasswordHashing.Verify(passwordB64, "not-base64", Convert.ToBase64String(salt)));
    }

    [TestMethod]
    public void Verify_Throws_For_Invalid_Base64_Salt()
    {
        var passwordB64 = B64("X");
        var (hash, _) = PasswordHashing.Hash(passwordB64);

        Assert.ThrowsExactly<FormatException>(() => PasswordHashing.Verify(passwordB64, Convert.ToBase64String(hash), "not-base64"));
    }

    // Helper to recompute using the same salt via the public surface.
    private static byte[] InvokePrivateHash(string passwordB64, byte[] salt)
    {
        // Re-create the expected hash by calling Verify against a dummy and reading its behavior:
        // Since Verify is boolean-only, we instead reconstruct with the same PBKDF2 parameters inline.
        // Keep this in sync with implementation if you change iterations/alg.
        byte[] password = Convert.FromBase64String(passwordB64);
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
    }
}
