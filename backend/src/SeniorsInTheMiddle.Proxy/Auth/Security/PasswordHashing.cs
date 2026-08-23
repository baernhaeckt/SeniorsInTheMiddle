using System.Security.Cryptography;

namespace SeniorsInTheMiddle.Proxy.Auth.Security
{
    /// <summary>
    /// Salted PBKDF2 password hashing, and the constant-time comparison that verifies against it.
    /// </summary>
    public static class PasswordHashing
    {
        public static bool Verify(string password, string hash, string salt)
        {
            byte[] hashToVerify = Hash(password, Convert.FromBase64String(salt));
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(hash), hashToVerify);
        }

        public static (byte[] Hash, byte[] Salt) Hash(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Hash(password, salt);
            return (hash, salt);
        }

        private static byte[] Hash(string password, byte[] salt)
        {
            // Clients send the password base64-encoded, so the raw bytes are what get derived
            // from. Feeding the encoded text in instead would silently change every hash.
            byte[] passwordBytes = Convert.FromBase64String(password);
            return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, 100_000, HashAlgorithmName.SHA256, 32);
        }
    }
}
