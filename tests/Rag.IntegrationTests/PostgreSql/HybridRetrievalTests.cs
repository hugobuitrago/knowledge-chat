using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rag.Application.Retrieval;
using Rag.Domain.Entities;
using Rag.Infrastructure.Ingestion;
using Rag.Infrastructure.Persistence;

namespace Rag.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class HybridRetrievalTests(PostgreSqlFixture database)
{
    private const string SemanticQuery = "conceptual beacon";
    private const string LimitQuery = "document limit probe";

    [Fact]
    public async Task Hybrid_retrieval_finds_lexical_codes_and_semantic_matches()
    {
        database.EmbeddingProvider.Reset();
        RetrievalSeed seed = await SeedAsync();
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        IHybridRetrievalService service = scope.ServiceProvider
            .GetRequiredService<IHybridRetrievalService>();

        RetrievalResult lexical = (await service.RetrieveAsync(
            new RetrievalCommand(
                seed.TenantId,
                seed.KnowledgeBaseId,
                ChatbotId: null,
                "ZX-81"),
            CancellationToken.None))!;
        RetrievalResult semantic = (await service.RetrieveAsync(
            new RetrievalCommand(
                seed.TenantId,
                seed.KnowledgeBaseId,
                ChatbotId: null,
                SemanticQuery),
            CancellationToken.None))!;

        Assert.False(lexical.Degraded);
        Assert.Equal(seed.VersionId, lexical.VersionId);
        Assert.Contains(lexical.Chunks, chunk => chunk.ChunkId == seed.LexicalChunkId);
        Assert.Equal(seed.SemanticChunkId, semantic.Chunks[0].ChunkId);
        Assert.All(
            semantic.Chunks,
            chunk => Assert.InRange(chunk.Score, double.Epsilon, 1D));
    }

    [Fact]
    public async Task Embedding_failure_returns_lexical_results_as_degraded()
    {
        database.EmbeddingProvider.Reset();
        RetrievalSeed seed = await SeedAsync();
        database.EmbeddingProvider.FailOnCall = 1;
        try
        {
            await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
            IHybridRetrievalService service = scope.ServiceProvider
                .GetRequiredService<IHybridRetrievalService>();

            RetrievalResult result = (await service.RetrieveAsync(
                new RetrievalCommand(
                    seed.TenantId,
                    seed.KnowledgeBaseId,
                    ChatbotId: null,
                    "ZX-81"),
                CancellationToken.None))!;

            Assert.True(result.Degraded);
            Assert.Contains(result.Chunks, chunk => chunk.ChunkId == seed.LexicalChunkId);
        }
        finally
        {
            database.EmbeddingProvider.Reset();
        }
    }

    [Fact]
    public async Task Retrieval_filters_tenant_active_version_and_limits_each_document()
    {
        database.EmbeddingProvider.Reset();
        RetrievalSeed seed = await SeedAsync();
        RetrievalSeed foreign = await SeedAsync();
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        IHybridRetrievalService service = scope.ServiceProvider
            .GetRequiredService<IHybridRetrievalService>();

        RetrievalResult result = (await service.RetrieveAsync(
            new RetrievalCommand(
                seed.TenantId,
                seed.KnowledgeBaseId,
                ChatbotId: null,
                LimitQuery),
            CancellationToken.None))!;
        RetrievalResult? crossTenant = await service.RetrieveAsync(
            new RetrievalCommand(
                seed.TenantId,
                foreign.KnowledgeBaseId,
                ChatbotId: null,
                LimitQuery),
            CancellationToken.None);

        Assert.Null(crossTenant);
        Assert.DoesNotContain(
            result.Chunks,
            chunk => chunk.ChunkId == seed.ArchivedChunkId);
        Assert.InRange(
            result.Chunks.Count(chunk => chunk.DocumentId == seed.LimitedDocumentId),
            1,
            2);
        Assert.InRange(result.Chunks.Count, 1, 8);
    }

    [Fact]
    public async Task User_query_is_parameterized_and_cannot_change_the_schema()
    {
        database.EmbeddingProvider.Reset();
        RetrievalSeed seed = await SeedAsync();
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        IHybridRetrievalService service = scope.ServiceProvider
            .GetRequiredService<IHybridRetrievalService>();

        RetrievalResult? result = await service.RetrieveAsync(
            new RetrievalCommand(
                seed.TenantId,
                seed.KnowledgeBaseId,
                ChatbotId: null,
                "ZX-81'); DROP TABLE tenants; --"),
            CancellationToken.None);
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();

        Assert.NotNull(result);
        Assert.True(await context.Tenants.AnyAsync(
            tenant => tenant.Id == seed.TenantId,
            CancellationToken.None));
    }

    private async Task<RetrievalSeed> SeedAsync()
    {
        float[] semanticVector = await CreateVectorAsync(SemanticQuery);
        float[] limitVector = await CreateVectorAsync(LimitQuery);
        float[] distantVector = semanticVector.Select(static value => -value).ToArray();
        Guid tenantId = Guid.NewGuid();
        Guid knowledgeBaseId = Guid.NewGuid();
        Guid versionId = Guid.NewGuid();
        Guid archivedVersionId = Guid.NewGuid();
        Guid semanticDocumentId = Guid.NewGuid();
        Guid lexicalDocumentId = Guid.NewGuid();
        Guid limitedDocumentId = Guid.NewGuid();
        Guid archivedDocumentId = Guid.NewGuid();
        Guid semanticChunkId = Guid.NewGuid();
        Guid lexicalChunkId = Guid.NewGuid();
        Guid archivedChunkId = Guid.NewGuid();

        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        context.AddRange(
            Tenant.Create(tenantId, $"retrieval-tenant-{tenantId:N}"),
            KnowledgeBase.Create(
                knowledgeBaseId,
                tenantId,
                $"retrieval-kb-{knowledgeBaseId:N}"));
        await context.SaveChangesAsync();

        KnowledgeBaseVersion active = CreateReadyVersion(
            versionId,
            tenantId,
            knowledgeBaseId);
        Document semanticDocument = CreateIndexedDocument(
            semanticDocumentId,
            tenantId,
            knowledgeBaseId,
            versionId,
            "semantic.txt");
        Document lexicalDocument = CreateIndexedDocument(
            lexicalDocumentId,
            tenantId,
            knowledgeBaseId,
            versionId,
            "codes.txt");
        Document limitedDocument = CreateIndexedDocument(
            limitedDocumentId,
            tenantId,
            knowledgeBaseId,
            versionId,
            "limit.txt");
        context.AddRange(
            active,
            semanticDocument,
            lexicalDocument,
            limitedDocument,
            CreateChunk(
                semanticChunkId,
                tenantId,
                knowledgeBaseId,
                versionId,
                semanticDocumentId,
                chunkIndex: 0,
                "Navigation guidance expressed without the query vocabulary.",
                semanticVector),
            CreateChunk(
                lexicalChunkId,
                tenantId,
                knowledgeBaseId,
                versionId,
                lexicalDocumentId,
                chunkIndex: 0,
                "Hardware identifier ZX-81 appears in the maintenance catalog.",
                distantVector));
        for (int index = 0; index < 3; index++)
        {
            context.DocumentChunks.Add(CreateChunk(
                Guid.NewGuid(),
                tenantId,
                knowledgeBaseId,
                versionId,
                limitedDocumentId,
                index,
                $"Independent passage number {index}.",
                limitVector));
        }

        KnowledgeBaseVersion archived = CreateReadyVersion(
            archivedVersionId,
            tenantId,
            knowledgeBaseId);
        archived.Archive();
        Document archivedDocument = CreateIndexedDocument(
            archivedDocumentId,
            tenantId,
            knowledgeBaseId,
            archivedVersionId,
            "archived.txt");
        context.AddRange(
            archived,
            archivedDocument,
            CreateChunk(
                archivedChunkId,
                tenantId,
                knowledgeBaseId,
                archivedVersionId,
                archivedDocumentId,
                chunkIndex: 0,
                "ARCHIVED-ONLY-991",
                semanticVector));
        await context.SaveChangesAsync();
        active.Activate();
        await context.SaveChangesAsync();

        return new RetrievalSeed(
            tenantId,
            knowledgeBaseId,
            versionId,
            semanticChunkId,
            lexicalChunkId,
            archivedChunkId,
            limitedDocumentId);
    }

    private static KnowledgeBaseVersion CreateReadyVersion(
        Guid versionId,
        Guid tenantId,
        Guid knowledgeBaseId)
    {
        KnowledgeBaseVersion version = KnowledgeBaseVersion.Create(
            versionId,
            tenantId,
            knowledgeBaseId,
            "integration-test-model",
            RagDatabaseConstants.EmbeddingDimensions);
        version.MarkProcessing();
        version.MarkReady();
        return version;
    }

    private static Document CreateIndexedDocument(
        Guid documentId,
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        string fileName)
    {
        Document document = Document.Create(
            documentId,
            tenantId,
            knowledgeBaseId,
            versionId,
            fileName,
            $"retrieval-tests/{documentId:N}.txt",
            "text/plain",
            Hash(fileName),
            fileName.Length);
        document.MarkProcessing();
        document.MarkIndexed();
        return document;
    }

    private static DocumentChunk CreateChunk(
        Guid chunkId,
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        Guid documentId,
        int chunkIndex,
        string content,
        float[] embedding) =>
        DocumentChunk.Create(
            chunkId,
            tenantId,
            knowledgeBaseId,
            versionId,
            documentId,
            chunkIndex,
            content,
            Hash(content),
            tokenCount: content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            startOffset: 0,
            endOffset: content.Length,
            Hash("retrieval-test-configuration"),
            embedding);

    private static async Task<float[]> CreateVectorAsync(string input)
    {
        var provider = new DeterministicEmbeddingProvider(
            new EmbeddingMetadataOptions
            {
                Provider = "Deterministic",
                Model = "integration-test-model",
                Dimensions = RagDatabaseConstants.EmbeddingDimensions,
            });
        return (await provider.GenerateAsync([input], CancellationToken.None))
            .Vectors[0]
            .ToArray();
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record RetrievalSeed(
        Guid TenantId,
        Guid KnowledgeBaseId,
        Guid VersionId,
        Guid SemanticChunkId,
        Guid LexicalChunkId,
        Guid ArchivedChunkId,
        Guid LimitedDocumentId);
}
