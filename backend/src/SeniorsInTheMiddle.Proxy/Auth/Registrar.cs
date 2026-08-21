using SeniorsInTheMiddle.Proxy.Auth.Security;
using SeniorsInTheMiddle.Proxy.Auth.Storage;

namespace SeniorsInTheMiddle.Proxy.Auth;

public static class Registrar
{
    public static void AddAuthServices(this IServiceCollection services)
    {
        services.AddScoped<IJwtFactory, JwtFactory>();
        services.AddSingleton<IUserStore, InMemoryUserStore>();
    }
}
