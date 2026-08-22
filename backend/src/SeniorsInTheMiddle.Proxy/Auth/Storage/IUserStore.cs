using SeniorsInTheMiddle.Proxy.Auth.Domain;

namespace SeniorsInTheMiddle.Proxy.Auth.Storage;

public interface IUserStore
{
    Task<User?> FindByUsernameAsync(string username);

    /// <summary>
    /// Creates <paramref name="user"/>, or returns false when the username or the email is
    /// already taken.
    ///
    /// One call rather than a lookup followed by a save, because the two together are not
    /// atomic: two registrations racing on the same name both find nothing and both save, and
    /// the second silently replaces the first one's password.
    /// </summary>
    Task<bool> TryCreateAsync(User user, string password);

    /// <summary>Creates or replaces <paramref name="user"/>. For seeding, where the caller has
    /// already decided the account should exist with this password.</summary>
    Task SaveAsync(User user, string password);

    Task<User?> VerifyPassword(string username, string password);
}
