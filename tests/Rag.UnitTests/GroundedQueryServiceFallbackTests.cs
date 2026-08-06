using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Rag.Application.Generation;
using Rag.Application.Providers;
using Rag.Application.Retrieval;
using Rag.Infrastructure.Generation;

namespace Rag.UnitTests;

public sealed class GroundedQueryServiceFallbackTests
{
    [Fact]
    public async Task Configured_secondary_provider_is_used_after_primary_failure()
    {
        Guid chunkId = Guid.NewGuid();
        var retrieval = new StubRetrievalService(new RetrievedChunk(
            chunkId,
            Guid.NewGuid(),
            "source.txt",
            0,
            0,
            24,
            "The supported value is 42.",
            0.25D));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Generation:Provider"] = "Deterministic",
                ["Generation:Model"] = "primary-test-model",
                ["Generation:FallbackMode"] = "SecondaryProvider",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRagGeneration(configuration, new TestEnvironment());
        services.RemoveAll<ILanguageModelProvider>();
        services.AddSingleton<IHybridRetrievalService>(retrieval);
        services.AddSingleton<ILanguageModelProvider, FailingProvider>();
        services.AddSingleton<ISecondaryLanguageModelProvider>(
            new SecondaryProvider(chunkId));
        await using ServiceProvider provider = services.BuildServiceProvider(
            validateScopes: true);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IQueryService service = scope.ServiceProvider
            .GetRequiredService<IQueryService>();

        QueryResult result = (await service.QueryAsync(
            new QueryCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                ChatbotId: null,
                "What is the value?",
                History: []),
            CancellationToken.None))!;

        Assert.True(result.Degraded);
        Assert.Equal("secondary-test-model", result.Model);
        Assert.Equal("The secondary answer is grounded.", result.Answer);
        Assert.Equal(chunkId, Assert.Single(result.Citations).ChunkId);
    }

    private sealed class StubRetrievalService(RetrievedChunk chunk) :
        IHybridRetrievalService
    {
        public ValueTask<RetrievalResult?> RetrieveAsync(
            RetrievalCommand command,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<RetrievalResult?>(new RetrievalResult(
                command.KnowledgeBaseId,
                Guid.NewGuid(),
                Degraded: false,
                [chunk]));
    }

    private sealed class FailingProvider : ILanguageModelProvider
    {
        public ValueTask<LanguageModelResult> GenerateAsync(
            LanguageModelRequest request,
            CancellationToken cancellationToken) =>
            throw new LanguageModelProviderException(
                "Primary provider failed.",
                isTransient: true);
    }

    private sealed class SecondaryProvider(Guid chunkId) :
        ISecondaryLanguageModelProvider
    {
        public ValueTask<LanguageModelResult> GenerateAsync(
            LanguageModelRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LanguageModelResult(
                "secondary-test-model",
                "The secondary answer is grounded.",
                100,
                10,
                [chunkId]));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Rag.UnitTests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
