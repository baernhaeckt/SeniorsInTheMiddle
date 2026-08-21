using SeniorsInTheMiddle.Proxy.Auth.Storage;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests.Integration;

public class CustomWebApplicationFactory<TProgram>
    : WebApplicationFactory<TProgram> where TProgram : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            ServiceDescriptor userStoreRegistration = services.First(
                d => d.ServiceType ==
                    typeof(IUserStore));

            services.Remove(userStoreRegistration);

            services.AddSingleton<IUserStore, InMemoryUserStore>();
        });

        builder.UseEnvironment("Development");
    }
}
