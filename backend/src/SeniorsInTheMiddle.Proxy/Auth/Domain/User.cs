namespace SeniorsInTheMiddle.Proxy.Auth.Domain;

/// <summary>
/// An account, without any secret. Password material never leaves the store, so nothing that
/// handles a <see cref="User"/> can leak it.
/// </summary>
public sealed record User(string Username, string Email);
