using SeniorsInTheMiddle.Proxy.Auth.Domain;

namespace SeniorsInTheMiddle.Proxy.Auth.Storage;

/// <summary>
/// The account store behind the auth endpoints. Passwords go in and are verified against;
/// they never come back out.
/// </summary>
public interface IUserStore
{
    Task<User?> FindByUsernameAsync(string username);

    /// <summary>
    /// Creates <paramref name="user"/>, or returns false when the username or the email is
    /// already taken.
    ///
    /// One call rather than a lookup followed by a save: two registrations racing on the same
    /// name would both find nothing, and the second would replace the first one's password.
    /// </summary>
    Task<bool> TryCreateAsync(User user, string password);

    /// <summary>Creates or replaces <paramref name="user"/>. For seeding, where the caller has
    /// already decided the account should exist with this password.</summary>
    Task SaveAsync(User user, string password);

    Task<User?> VerifyPassword(string username, string password);
}
