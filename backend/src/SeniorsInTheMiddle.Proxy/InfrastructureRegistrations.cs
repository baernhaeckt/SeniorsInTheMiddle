using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

using System.Text;

namespace SeniorsInTheMiddle.Proxy;

public static class InfrastructureRegistrations
{
    public static IServiceCollection AddSwaggerWithJwt(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            // Login possibilty for Swagger UI
            options.AddDocumentTransformer<SecuritySchemeTransformer>();

            // This is necessary to let the UI use relative path and hence "https"
            options.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Servers?.Clear();
                return Task.CompletedTask;
            });
        });

        return services;
    }


    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(configuration["Jwt:Key"] ?? throw new InvalidOperationException())),
                ValidateIssuer = true,
                ValidIssuer = configuration["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = configuration["Jwt:Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(5)
            };
        });

        services.AddAuthorization();

        return services;
    }

    /// <summary>
    /// The SPA is served from its own container on a different origin, so every call it
    /// makes to this app is cross-origin.
    ///
    /// The origins are listed explicitly rather than allowed wildcard, because the SignalR
    /// JavaScript client sends its negotiate request with credentials, and a browser
    /// refuses a credentialed response whose Access-Control-Allow-Origin is "*".
    /// </summary>
    public static IServiceCollection AddAppCors(this IServiceCollection services, IConfiguration configuration)
    {
        string[] origins = AllowedOrigins(configuration);

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins(origins)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials();
            });
        });

        return services;
    }


    /// <summary>
    /// Browsers compare origins without a trailing slash, so a configured
    /// "https://example.com/" would silently never match anything.
    /// </summary>
    private static string[] AllowedOrigins(IConfiguration configuration)
        => [.. (configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Select(origin => origin.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    public static void RegisterMiddlewares(this WebApplication app)
    {
        // Serve OpenAPI JSON
        app.MapOpenApi();

        // Add Docs
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/v1.json", "v1");
        });

        // Health check endpoint
        app.MapGet("/health", () =>
        {
            return Results.NoContent();
        }).WithTags("Health");

        // Add Cors headers. Logged because a missing origin surfaces in the browser as an
        // opaque network error, with nothing on the server to point at it.
        string[] origins = AllowedOrigins(app.Configuration);
        if (origins.Length == 0)
        {
            app.Logger.LogWarning(
                "No Cors:AllowedOrigins configured. Browsers will block every cross-origin "
                + "call from the dashboard.");
        }
        else
        {
            app.Logger.LogInformation("CORS allows {Origins}.", string.Join(", ", origins));
        }

        app.UseCors();
        // Add authentication and authorization
        app.UseAuthentication();
        app.UseAuthorization();
    }
}
internal sealed class SecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer", // "bearer" refers to the header name here
            In = ParameterLocation.Header,
            BearerFormat = "Json Web Token",
            Description = "Jwt authentication"
        };

        // Iterate through each path & operation
        foreach (IOpenApiPathItem path in document.Paths.Values)
        {
#pragma warning disable CS8602 // Dereference of a possibly null reference.
            foreach (OpenApiOperation operation in path.Operations.Values)
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("Bearer", document)] = []
                });
            }
#pragma warning restore CS8602 // Dereference of a possibly null reference.
        }

        return Task.CompletedTask;
    }
}
