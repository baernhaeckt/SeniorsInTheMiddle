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
    private readonly IUserStore _users;
    private readonly SeedUserOptions _options;
    private readonly ILogger<UserSeeder> _logger;

    public UserSeeder(IUserStore users, IOptions<SeedUserOptions> options, ILogger<UserSeeder> logger)
    {
        _users = users;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogInformation(
                "No {Section}:Username configured, so no account is seeded. Everyone "
                + "self-registers, and a restart clears them again.",
                SeedUserOptions.SectionName);
            return;
        }

        // Registering over an existing account would reset a password someone had changed.
        if (await _users.FindByUsernameAsync(_options.Username) is not null)
            return;

        await _users.SaveAsync(new User(_options.Username, _options.Email), _options.Password);

        if (_options.Advertise)
        {
            _logger.LogWarning(
                "Seeded the account {Username} and is handing its password to anyone who asks "
                + "at /api/v1/auth/demo-account, so the login screen can prefill it. Intended "
                + "for demos. Set {Section}:Advertise to false anywhere real traffic flows.",
                _options.Username,
                SeedUserOptions.SectionName);
        }
        else
        {
            _logger.LogInformation("Seeded the account {Username}.", _options.Username);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
