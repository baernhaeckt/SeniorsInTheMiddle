using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using SeniorsInTheMiddle.Proxy.Services.Pii;
using SeniorsInTheMiddle.Proxy.Services.PrivacyCheck;

namespace SeniorsInTheMiddle.Proxy.Services;

public static class Registrar
{
    public const string HealthPath = "/healthz";

    /// <summary>
    /// The python services this process talks to over unix sockets, one socket each,
    /// configured under <c>Services:&lt;Name&gt;:SocketPath</c>. Adding a service: a name in
    /// <see cref="ServiceConnections.KnownServices"/>, a typed client here, and the socket
    /// path in backend/Dockerfile next to its supervisord program.
    /// </summary>
    public static IServiceCollection AddPythonServices(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddSingleton(ServiceOptions.From(configuration))
            .AddSingleton<ServiceConnections>()
            .AddSingleton<IPiiServiceClient, PiiServiceClient>()
            .AddSingleton<IPrivacyCheckServiceClient, PrivacyCheckServiceClient>()
            .AddHostedService<ServiceStartupProbe>();

        services.AddHealthChecks()
            .AddCheck<ServiceHealthCheck>("services", tags: ["services"]);

        return services;
    }

    /// <summary>
    /// <c>GET /healthz</c>: 200 while every configured service answers, 503 otherwise, with
    /// one line per service. Anonymous, so a container platform can probe it. It lives on
    /// the API port only: <see cref="Forwarding.ProxyPortGuard"/> answers 404 for it on the
    /// proxy listeners.
    /// </summary>
    public static void MapServiceHealth(this IEndpointRouteBuilder routes)
    {
        routes.MapHealthChecks(HealthPath, new HealthCheckOptions
        {
            ResponseWriter = WriteAsync,
        })
            .AllowAnonymous();
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            services = report.Entries
                .SelectMany(entry => entry.Value.Data)
                .ToDictionary(item => item.Key, item => item.Value),
        });
    }
}
