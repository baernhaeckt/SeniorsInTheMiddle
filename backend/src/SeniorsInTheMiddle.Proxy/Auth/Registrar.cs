using SeniorsInTheMiddle.Proxy.Auth.Security;
using SeniorsInTheMiddle.Proxy.Auth.Storage;

namespace SeniorsInTheMiddle.Proxy.Auth;

/// <summary>Wires up the authentication stack: token issuing, the user store and the demo seeder.</summary>
public static class Registrar
{
    public static void AddAuthServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IJwtFactory, JwtFactory>();
        services.AddSingleton<IUserStore, InMemoryUserStore>();

        services.Configure<SeedUserOptions>(configuration.GetSection(SeedUserOptions.SectionName));
        services.AddHostedService<UserSeeder>();
    }
}
