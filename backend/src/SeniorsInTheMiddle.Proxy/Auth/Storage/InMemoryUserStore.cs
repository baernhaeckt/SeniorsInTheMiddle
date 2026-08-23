using System.Collections.Concurrent;
using System.Text;

using SeniorsInTheMiddle.Proxy.Auth.Domain;
using SeniorsInTheMiddle.Proxy.Auth.Security;

namespace SeniorsInTheMiddle.Proxy.Auth.Storage;

/// <summary>
/// Accounts for as long as the process lives. A restart starts empty, which is why
/// <see cref="UserSeeder"/> exists.
///
/// Passwords are kept as a PBKDF2 hash and its salt, never in the clear: in-memory still
/// reaches a crash dump, a debugger, and anything that later swaps this for a real store.
/// </summary>
public class InMemoryUserStore : IUserStore
{
    /// <summary>Keyed by username; the email index is a scan, see <see cref="IsEmailTaken"/>.</summary>
    private readonly ConcurrentDictionary<string, Credentials> _users = new();

    /// <summary>
    /// Held for writes only, so lookups stay lock-free.
    ///
    /// <see cref="TryCreateAsync"/> must check "is this name or address taken" and act on the
    /// answer as one step. ConcurrentDictionary makes each operation atomic, not the pair.
    /// </summary>
    private readonly Lock _writeLock = new();

    /// <summary>What is actually stored per account: the user plus base64 PBKDF2 hash and salt.</summary>
    private sealed record Credentials(User User, string Hash, string Salt);

    public Task<User?> FindByUsernameAsync(string username)
    {
        if (_users.TryGetValue(username, out Credentials? entry))
            return Task.FromResult<User?>(entry.User);

        return Task.FromResult<User?>(null);
    }

    public Task<bool> TryCreateAsync(User user, string password)
    {
        lock (_writeLock)
        {
            if (_users.ContainsKey(user.Username) || IsEmailTaken(user.Email))
                return Task.FromResult(false);

            _users[user.Username] = Hashed(user, password);
            return Task.FromResult(true);
        }
    }

    public Task SaveAsync(User user, string password)
    {
        lock (_writeLock)
        {
            _users[user.Username] = Hashed(user, password);
        }

        return Task.CompletedTask;
    }

    public Task<User?> VerifyPassword(string username, string password)
    {
        if (_users.TryGetValue(username, out Credentials? entry) &&
            PasswordHashing.Verify(Encoded(password), entry.Hash, entry.Salt))
        {
            return Task.FromResult<User?>(entry.User);
        }

        return Task.FromResult<User?>(null);
    }

    /// <summary>For tests: put an account in place without going through registration.</summary>
    public void Seed(User user, string password)
    {
        lock (_writeLock)
        {
            _users[user.Username] = Hashed(user, password);
        }
    }

    /// <summary>For tests: drop every account, so one test class cannot see another's.</summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            _users.Clear();
        }
    }

    /// <summary>
    /// Whether any account already uses <paramref name="email"/>.
    ///
    /// A scan, because the store is keyed by username alone. Registration is rare and one
    /// process holds few accounts; a store that grows past that wants a second index.
    /// </summary>
    private bool IsEmailTaken(string email)
        => _users.Values.Any(entry => string.Equals(entry.User.Email, email, StringComparison.OrdinalIgnoreCase));

    private static Credentials Hashed(User user, string password)
    {
        (byte[] hash, byte[] salt) = PasswordHashing.Hash(Encoded(password));
        return new Credentials(user, Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    /// <summary>
    /// <see cref="PasswordHashing"/> takes its password base64-encoded rather than as text, a
    /// contract its unit tests pin. Encoding here keeps that helper untouched.
    /// </summary>
    private static string Encoded(string password)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
}
