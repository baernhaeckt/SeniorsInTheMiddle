using System.Security.Cryptography;

namespace Backend.Web.Auth.Security
{
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
            byte[] passwordBytes = Convert.FromBase64String(password);
            return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, 100_000, HashAlgorithmName.SHA256, 32);
        }
    }
}
