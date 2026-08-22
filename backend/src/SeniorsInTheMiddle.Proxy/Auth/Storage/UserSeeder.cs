using Microsoft.Extensions.Options;

using SeniorsInTheMiddle.Proxy.Auth.Domain;

namespace SeniorsInTheMiddle.Proxy.Auth.Storage;

/// <summary>
/// Creates the configured demo account once, at startup.
///
/// Runs as a hosted service rather than during registration so it resolves whichever
/// <see cref="IUserStore"/> the app actually ended up with — including the one the test
/// factory substitutes.
/// </summary>
sealed class UserSeeder : IHostedService
{
    private readonly IUserStore users;
    private readonly SeedUserOptions options;
    private readonly ILogger<UserSeeder> logger;

    public UserSeeder(IUserStore users, IOptions<SeedUserOptions> options, ILogger<UserSeeder> logger)
    {
        this.users = users;
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            logger.LogInformation(
                "No {Section}:Username configured, so no account is seeded. Everyone "
                + "self-registers, and a restart clears them again.",
                SeedUserOptions.SectionName);
            return;
        }

        // Registering over an existing account would reset a password someone had changed.
        if (await users.FindByUsernameAsync(options.Username) is not null)
            return;

        await users.SaveAsync(new User(options.Username, options.Email), options.Password);

        if (options.Advertise)
        {
            logger.LogWarning(
                "Seeded the account {Username} and is handing its password to anyone who asks "
                + "at /api/v1/auth/demo-account, so the login screen can prefill it. Intended "
                + "for demos. Set {Section}:Advertise to false anywhere real traffic flows.",
                options.Username,
                SeedUserOptions.SectionName);
        }
        else
        {
            logger.LogInformation("Seeded the account {Username}.", options.Username);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
