using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.Application.Generation;
using Rag.Application.Providers;

namespace Rag.Infrastructure.Generation;

public static class GenerationExtensions
{
    public static IServiceCollection AddRagGeneration(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services
            .AddOptions<GenerationOptions>()
            .Bind(configuration.GetSection(GenerationOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static options => Enum.IsDefined(options.FallbackMode),
                "Generation:FallbackMode is not supported.")
            .Validate(
                options => environment.IsDevelopment() &&
                    string.Equals(
                        options.Provider,
                        "Deterministic",
                        StringComparison.Ordinal),
                "Only the Deterministic language model provider is available, and it is restricted to Development.")
            .ValidateOnStart();
        services.AddSingleton<ILanguageModelProvider, DeterministicLanguageModelProvider>();
        services.AddScoped<IQueryService, GroundedQueryService>();
        return services;
    }
}
