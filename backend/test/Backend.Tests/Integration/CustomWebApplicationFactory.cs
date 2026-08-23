using SeniorsInTheMiddle.Proxy.Auth.Storage;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests.Integration;

/// <summary>
/// Hosts the API in-process for integration tests, with the user store swapped for a fresh
/// instance so accounts never leak from one test class into the next.
/// </summary>
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
