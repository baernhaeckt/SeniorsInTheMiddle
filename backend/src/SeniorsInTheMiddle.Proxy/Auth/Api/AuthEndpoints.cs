using SeniorsInTheMiddle.Proxy.Auth.Domain;
using SeniorsInTheMiddle.Proxy.Auth.Security;
using SeniorsInTheMiddle.Proxy.Auth.Storage;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace SeniorsInTheMiddle.Proxy.Auth.Api;

public static class AuthEndpoints
{
    public static void RegisterAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder auth = routes.MapGroup("/api/v1/auth");

        // Login endpoint
        auth.MapPost("/login", async (
            [FromBody] LoginRequest request,
            IUserStore userStore,
            IJwtFactory jwtService) =>
        {
            if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
            {
                return Results.BadRequest(new { message = "Username and password are required" });
            }

            User? user = await userStore.VerifyPassword(request.Username, request.Password);
            if (user == null)
            {
                return Results.Unauthorized();
            }

            string token = jwtService.GenerateToken(user);

            return Results.Ok(new LoginResponse(token));
        })
        .WithName("Login")
        .WithTags("Authentication")
        // Anonymous, or there would be no way to obtain the token this asks for.
        .AllowAnonymous();

        // Register endpoint
        auth.MapPost("/register", async (
            [FromBody] RegisterRequest request,
            IUserStore userStore) =>
        {
            if (string.IsNullOrEmpty(request.Username) ||
                string.IsNullOrEmpty(request.Email) ||
                string.IsNullOrEmpty(request.Password))
            {
                return Results.BadRequest(new { message = "All fields are required" });
            }

            // Deliberately lax: accounts live in memory for one demo and nothing else. A
            // deployment that keeps real accounts wants a password policy, an e-mail format
            // check and a rate limit on /login before any of this is reused.
            if (request.Password.Length < 4)
            {
                return Results.BadRequest(new { message = "Password must be at least 4 characters long" });
            }

            // One call, because checking and then saving lets two registrations racing on the
            // same name both pass the check and the second overwrite the first one's password.
            bool created = await userStore.TryCreateAsync(
                new User(request.Username, request.Email),
                request.Password);

            if (!created)
            {
                return Results.BadRequest(new { message = "Username or email already exists" });
            }

            return Results.Ok(new { message = "User registered successfully" });
        })
        .WithName("Register")
        .WithTags("Authentication")
        // Self-registration: the whole point is that the caller has no account yet.
        .AllowAnonymous();

        // What the login screen prefills its fields from, so a demo is one click rather than
        // a typed password.
        //
        // This hands out a working credential, so it answers only when an operator has both
        // seeded an account and explicitly switched advertising on. It is off unless
        // configured, and it must stay off anywhere real household traffic is flowing.
        auth.MapGet("/demo-account", (IOptions<SeedUserOptions> options) =>
        {
            SeedUserOptions seed = options.Value;

            return seed.Advertise && seed.IsConfigured
                ? Results.Ok(new DemoAccountResponse(seed.Username, seed.Password))
                : Results.NotFound();
        })
        .WithName("GetDemoAccount")
        .WithTags("Authentication")
        .AllowAnonymous();

        // Get current user endpoint (protected)
        auth.MapGet("/me", async (
            HttpContext context,
            IUserStore userStore) =>
        {
            string? username = context.User.Identity?.Name;
            if (string.IsNullOrEmpty(username))
            {
                return Results.Unauthorized();
            }

            User? user = await userStore.FindByUsernameAsync(username);
            if (user == null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new ProfileResponse(username, user.Email));
        })
        .WithName("GetCurrentUser")
        .WithTags("Authentication")
        .RequireAuthorization();
    }
}
