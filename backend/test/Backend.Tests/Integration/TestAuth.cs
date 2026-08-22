using System.Net.Http.Json;

using SeniorsInTheMiddle.Proxy.Auth.Api;

namespace Backend.Tests.Integration;

/// <summary>
/// Registers a throwaway account and returns a token for it. Tests that need a signed-in
/// caller say so in one line rather than repeating the register-then-login dance.
/// </summary>
static class TestAuth
{
    public const string Username = "hub-tester";
    public const string Password = "Test123";
    public const string Email = "hub-tester@test.ch";

    public static async Task<string> TokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        HttpResponseMessage registered = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(Username, Email, Password),
            cancellationToken);
        registered.EnsureSuccessStatusCode();

        HttpResponseMessage loggedIn = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(Username, Password),
            cancellationToken);
        loggedIn.EnsureSuccessStatusCode();

        LoginResponse? response = await loggedIn.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);
        Assert.IsNotNull(response?.Token);

        return response.Token;
    }
}
