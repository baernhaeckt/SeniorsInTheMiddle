namespace SeniorsInTheMiddle.Proxy.Auth.Storage;

/// <summary>
/// An account created at startup, bound from <c>Auth:SeedUser</c>.
///
/// Users live in memory, so every restart starts with an empty store. Without a seeded
/// account, a restart mid-demo leaves nobody able to sign in to a dashboard on a wall.
/// </summary>
public sealed class SeedUserOptions
{
    public const string SectionName = "Auth:SeedUser";

    public string Username { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Whether <c>GET /api/v1/auth/demo-account</c> hands these credentials out, so the login
    /// screen can prefill them.
    ///
    /// Off by default, switched on only in Development: it publishes a working login to
    /// anyone who can reach the API. Fine for a demo box, never where real traffic flows.
    /// </summary>
    public bool Advertise { get; set; }

    /// <summary>
    /// Nothing is seeded unless there is at least a username and a password to seed with.
    /// </summary>
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
}
