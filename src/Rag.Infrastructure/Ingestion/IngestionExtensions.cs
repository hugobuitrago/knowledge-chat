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

        services.AddSingleton<IDocumentStorage, LocalDocumentStorage>();
        services.AddScoped<IDocumentUploadService, DocumentUploadService>();
        services.AddScoped<IIngestionJobQueue, PostgresIngestionJobQueue>();

        return services;
    }
}
