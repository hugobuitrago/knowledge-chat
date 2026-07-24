using Microsoft.AspNetCore.Authentication;
using Rag.Application.Security;

namespace Rag.Api.Security;

internal static class SecurityExtensions
{
    public static IServiceCollection AddRagSecurity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<
            Microsoft.Extensions.Options.IValidateOptions<ApiKeyAuthenticationOptions>,
            ApiKeyAuthenticationOptionsValidator>();
        services
            .AddOptions<ApiKeyAuthenticationOptions>()
            .Bind(configuration.GetSection(ApiKeyAuthenticationOptions.SectionName))
            .ValidateOnStart();

        services
            .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName,
                static _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(
                RagScopes.Admin,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireClaim(RagClaimTypes.Scope, RagScopes.Admin))
            .AddPolicy(
                RagScopes.Ingest,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireClaim(RagClaimTypes.Scope, RagScopes.Ingest))
            .AddPolicy(
                RagScopes.Retrieve,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireClaim(RagClaimTypes.Scope, RagScopes.Retrieve));

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentTenant, HttpCurrentTenant>();

        return services;
    }
}
