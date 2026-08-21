using Backend.Web.Auth.Security;
using Backend.Web.Auth.Storage;

namespace Backend.Web.Auth;

public static class Registrar
{
    public static void AddAuthServices(this IServiceCollection services)
    {
        services.AddScoped<IJwtFactory, JwtFactory>();
        services.AddSingleton<IUserStore, InMemoryUserStore>();
    }
}
