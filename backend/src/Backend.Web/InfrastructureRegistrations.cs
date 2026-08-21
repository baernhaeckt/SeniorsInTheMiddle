using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

using System.Text;

namespace Backend.Web;

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

    public static IServiceCollection AddAppCors(this IServiceCollection services)
    {
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(builder =>
            {
                builder.AllowAnyOrigin()
                       .AllowAnyMethod()
                       .AllowAnyHeader();
            });
        });
        return services;
    }

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

        // Add Cors headers
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
