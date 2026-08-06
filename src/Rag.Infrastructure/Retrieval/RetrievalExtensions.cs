using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rag.Application.Retrieval;

namespace Rag.Infrastructure.Retrieval;

public static class RetrievalExtensions
{
    public static IServiceCollection AddRagRetrieval(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<RetrievalOptions>()
            .Bind(configuration.GetSection(RetrievalOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static options => string.Equals(
                    options.TextSearchConfiguration,
                    "simple",
                    StringComparison.Ordinal),
                "Retrieval:TextSearchConfiguration must be 'simple' because the stored tsvector uses that controlled configuration.")
            .ValidateOnStart();
        services.AddSingleton<IChunkReranker, NoOpChunkReranker>();
        services.AddScoped<IHybridRetrievalService, HybridRetrievalService>();
        return services;
    }
}
