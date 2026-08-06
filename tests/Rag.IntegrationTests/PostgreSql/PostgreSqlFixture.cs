using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Rag.Application.Providers;
using Rag.Infrastructure.Ingestion;
using Rag.Infrastructure.Persistence;
using Rag.Infrastructure.Retrieval;
using Testcontainers.PostgreSql;

namespace Rag.IntegrationTests.PostgreSql;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(
            "pgvector/pgvector:0.8.5-pg18-bookworm")
        .WithDatabase("rag_tests")
        .WithUsername("rag_tests")
        .WithPassword("rag_tests_only")
        .Build();
    private readonly string _storageRoot = Path.Combine(
        Path.GetTempPath(),
        "rag-integration-storage",
        Guid.NewGuid().ToString("N"));

    public ServiceProvider Services { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    public string StorageRoot => _storageRoot;

    public CountingEmbeddingProvider EmbeddingProvider =>
        Services.GetRequiredService<CountingEmbeddingProvider>();

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = ConnectionString,
                ["Database:CommandTimeoutSeconds"] = "30",
                ["Chunking:MaxTokens"] = "500",
                ["Chunking:OverlapTokens"] = "80",
                ["Embedding:BatchSize"] = "2",
                ["Embedding:Dimensions"] = "1536",
                ["Embedding:MaxConcurrency"] = "2",
                ["Embedding:Model"] = "integration-test-model",
                ["Embedding:Provider"] = "Deterministic",
                ["Embedding:RequestTimeoutSeconds"] = "30",
                ["Jobs:BaseRetryDelaySeconds"] = "1",
                ["Jobs:LeaseDurationSeconds"] = "1",
                ["Jobs:MaxAttempts"] = "2",
                ["Jobs:MaxRetryDelaySeconds"] = "2",
                ["Storage:LocalPath"] = _storageRoot,
                ["Storage:Provider"] = "Local",
                ["Uploads:IdempotencyTtlHours"] = "24",
                ["Uploads:MaxFileSizeBytes"] = "1048576",
            })
            .Build();
        var services = new ServiceCollection();
        var environment = new TestHostEnvironment
        {
            ContentRootPath = Directory.GetCurrentDirectory(),
        };
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddRagPersistence(configuration);
        services.AddRagIngestion(configuration, environment);
        services.AddRagRetrieval(configuration);
        services.AddSingleton<CountingEmbeddingProvider>();
        services.AddSingleton<IEmbeddingProvider>(
            static provider =>
                provider.GetRequiredService<CountingEmbeddingProvider>());
        Services = services.BuildServiceProvider(validateScopes: true);

        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (Services is not null)
        {
            await Services.DisposeAsync().ConfigureAwait(false);
        }

        await _container.DisposeAsync().ConfigureAwait(false);
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Rag.IntegrationTests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

