using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.Application.Ingestion;
using Rag.Application.Providers;
using Rag.Infrastructure.Storage;

namespace Rag.Infrastructure.Ingestion;

public static class IngestionExtensions
{
    public static IServiceCollection AddRagIngestion(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services
            .AddOptions<UploadsOptions>()
            .Bind(configuration.GetSection(UploadsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services
            .AddOptions<JobsOptions>()
            .Bind(configuration.GetSection(JobsOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static options =>
                    options.MaxRetryDelaySeconds >= options.BaseRetryDelaySeconds,
                "Jobs:MaxRetryDelaySeconds must be greater than or equal to Jobs:BaseRetryDelaySeconds.")
            .ValidateOnStart();
        services
            .AddOptions<EmbeddingMetadataOptions>()
            .Bind(configuration.GetSection(EmbeddingMetadataOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => environment.IsDevelopment() &&
                    string.Equals(
                        options.Provider,
                        "Deterministic",
                        StringComparison.Ordinal),
                "Only the Deterministic embedding provider is available, and it is restricted to Development.")
            .ValidateOnStart();
        services
            .AddOptions<ChunkingOptions>()
            .Bind(configuration.GetSection(ChunkingOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static options => options.OverlapTokens < options.MaxTokens,
                "Chunking:OverlapTokens must be smaller than Chunking:MaxTokens.")
            .ValidateOnStart();
        services
            .AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => environment.IsDevelopment() &&
                    string.Equals(options.Provider, "Local", StringComparison.Ordinal),
                "Only the Local storage provider is available, and it is restricted to Development.")
            .ValidateOnStart();
        services
            .AddOptions<VersionMaintenanceOptions>()
            .Bind(configuration.GetSection(VersionMaintenanceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IDocumentStorage, LocalDocumentStorage>();
        services.AddSingleton<ITextChunker, ParagraphTextChunker>();
        services.AddSingleton<IEmbeddingProvider, DeterministicEmbeddingProvider>();
        services.AddSingleton<EmbeddingBatchExecutor>();
        services.AddScoped<IDocumentUploadService, DocumentUploadService>();
        services.AddScoped<IIngestionJobQueue, PostgresIngestionJobQueue>();
        services.AddScoped<IDocumentIngestionProcessor, DocumentIngestionProcessor>();
        services.AddScoped<IKnowledgeBaseVersionActivator, KnowledgeBaseVersionActivator>();
        services.AddScoped<IKnowledgeBaseVersionMaintenance, KnowledgeBaseVersionMaintenance>();

        return services;
    }

    public static IServiceCollection AddRagIngestionWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<IngestionWorkerOptions>()
            .Bind(configuration.GetSection(IngestionWorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHostedService<IngestionWorker>();
        services.AddHostedService<VersionMaintenanceWorker>();
        return services;
    }
}
