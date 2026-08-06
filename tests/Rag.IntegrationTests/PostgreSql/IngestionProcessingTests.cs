using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rag.Application.Ingestion;
using Rag.Application.Providers;
using Rag.Domain.Entities;
using Rag.Domain.Enums;
using Rag.Infrastructure.Ingestion;
using Rag.Infrastructure.Persistence;

namespace Rag.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class IngestionProcessingTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task Processing_persists_bounded_chunks_and_activates_version()
    {
        database.EmbeddingProvider.Reset();
        await ResetQueueAsync();
        SeededDocument seed = await SeedDocumentAsync(
            CreateContent(1_250),
            tenantId: null);

        await ProcessNextJobAsync();

        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        Document document = await context.Documents
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.DocumentId);
        KnowledgeBaseVersion version = await context.KnowledgeBaseVersions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.VersionId);
        IngestionJob job = await context.IngestionJobs
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.JobId);
        DocumentChunk[] chunks = await context.DocumentChunks
            .AsNoTracking()
            .ForKnowledgeBaseVersion(
                seed.TenantId,
                seed.KnowledgeBaseId,
                seed.VersionId)
            .OrderBy(static chunk => chunk.ChunkIndex)
            .ToArrayAsync();

        Assert.Equal(DocumentStatus.Indexed, document.Status);
        Assert.Equal(KnowledgeBaseVersionStatus.Active, version.Status);
        Assert.Equal(IngestionJobStatus.Completed, job.Status);
        Assert.True(chunks.Length >= 3);
        Assert.All(chunks, chunk => Assert.InRange(chunk.TokenCount, 1, 500));
        Assert.All(chunks, chunk => Assert.True(chunk.EndOffset > chunk.StartOffset));
        Assert.All(chunks, chunk => Assert.Equal(64, chunk.ContentHash.Length));
        Assert.All(
            chunks,
            chunk => Assert.Equal(64, chunk.ChunkingConfigurationHash.Length));
        Assert.Equal(
            chunks.Select(static chunk => chunk.ChunkIndex),
            Enumerable.Range(0, chunks.Length));
        Assert.Equal(chunks.Length, database.EmbeddingProvider.Inputs);
    }

    [Fact]
    public async Task Compatible_identical_chunks_reuse_embeddings_without_provider_calls()
    {
        database.EmbeddingProvider.Reset();
        await ResetQueueAsync();
        string content = CreateContent(900);
        SeededDocument first = await SeedDocumentAsync(content, tenantId: null);
        await ProcessNextJobAsync();
        int inputsAfterFirstDocument = database.EmbeddingProvider.Inputs;
        Assert.True(inputsAfterFirstDocument > 0);

        SeededDocument second = await SeedDocumentAsync(content, first.TenantId);
        await ProcessNextJobAsync();

        Assert.Equal(inputsAfterFirstDocument, database.EmbeddingProvider.Inputs);
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        DocumentChunk[] firstChunks = await context.DocumentChunks
            .AsNoTracking()
            .ForKnowledgeBaseVersion(
                first.TenantId,
                first.KnowledgeBaseId,
                first.VersionId)
            .OrderBy(static chunk => chunk.ChunkIndex)
            .ToArrayAsync();
        DocumentChunk[] secondChunks = await context.DocumentChunks
            .AsNoTracking()
            .ForKnowledgeBaseVersion(
                second.TenantId,
                second.KnowledgeBaseId,
                second.VersionId)
            .OrderBy(static chunk => chunk.ChunkIndex)
            .ToArrayAsync();
        Assert.Equal(firstChunks.Length, secondChunks.Length);
        for (int index = 0; index < firstChunks.Length; index++)
        {
            Assert.Equal(firstChunks[index].ContentHash, secondChunks[index].ContentHash);
            Assert.Equal(firstChunks[index].Embedding, secondChunks[index].Embedding);
        }
    }

    [Fact]
    public async Task Partial_provider_failure_persists_no_chunks_and_retry_does_not_duplicate()
    {
        database.EmbeddingProvider.Reset();
        database.EmbeddingProvider.FailOnCall = 2;
        await ResetQueueAsync();
        SeededDocument seed = await SeedDocumentAsync(
            CreateContent(1_250),
            tenantId: null);

        await using (AsyncServiceScope firstScope = database.Services.CreateAsyncScope())
        {
            IIngestionJobQueue queue =
                firstScope.ServiceProvider.GetRequiredService<IIngestionJobQueue>();
            IDocumentIngestionProcessor processor =
                firstScope.ServiceProvider.GetRequiredService<IDocumentIngestionProcessor>();
            IngestionJobLease lease = (await queue.TryAcquireAsync(
                CancellationToken.None))!;
            EmbeddingProviderException exception =
                await Assert.ThrowsAsync<EmbeddingProviderException>(
                    () => processor
                        .ProcessAsync(lease, CancellationToken.None)
                        .AsTask());
            await queue.FailAsync(
                lease,
                "The embedding provider failed.",
                exception.IsTransient,
                CancellationToken.None);
        }

        await using (AsyncServiceScope inspectionScope =
                     database.Services.CreateAsyncScope())
        {
            RagDbContext context =
                inspectionScope.ServiceProvider.GetRequiredService<RagDbContext>();
            Assert.Empty(
                await context.DocumentChunks
                    .Where(chunk => chunk.DocumentId == seed.DocumentId)
                    .ToArrayAsync());
            IngestionJob retrying = await context.IngestionJobs
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == seed.JobId);
            KnowledgeBaseVersion version = await context.KnowledgeBaseVersions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == seed.VersionId);
            Assert.Equal(IngestionJobStatus.Retrying, retrying.Status);
            Assert.Equal(KnowledgeBaseVersionStatus.Processing, version.Status);
        }

        database.EmbeddingProvider.FailOnCall = null;
        await Task.Delay(TimeSpan.FromMilliseconds(1_300));
        await ProcessNextJobAsync();

        await using AsyncServiceScope finalScope = database.Services.CreateAsyncScope();
        RagDbContext finalContext =
            finalScope.ServiceProvider.GetRequiredService<RagDbContext>();
        DocumentChunk[] chunks = await finalContext.DocumentChunks
            .Where(chunk => chunk.DocumentId == seed.DocumentId)
            .ToArrayAsync();
        IngestionJob completed = await finalContext.IngestionJobs
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.JobId);
        KnowledgeBaseVersion ready = await finalContext.KnowledgeBaseVersions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == seed.VersionId);
        Assert.NotEmpty(chunks);
        Assert.Equal(chunks.Length, chunks.Select(static chunk => chunk.ChunkIndex).Distinct().Count());
        Assert.Equal(IngestionJobStatus.Completed, completed.Status);
        Assert.Equal(KnowledgeBaseVersionStatus.Active, ready.Status);
    }

    private async Task ProcessNextJobAsync()
    {
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        IIngestionJobQueue queue =
            scope.ServiceProvider.GetRequiredService<IIngestionJobQueue>();
        IDocumentIngestionProcessor processor =
            scope.ServiceProvider.GetRequiredService<IDocumentIngestionProcessor>();
        IngestionJobLease lease = (await queue.TryAcquireAsync(
            CancellationToken.None))!;
        await processor.ProcessAsync(lease, CancellationToken.None);
    }

    private async Task ResetQueueAsync()
    {
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        await context.IngestionJobs.ExecuteDeleteAsync();
    }

    private async Task<SeededDocument> SeedDocumentAsync(
        string content,
        Guid? tenantId)
    {
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        IDocumentStorage storage =
            scope.ServiceProvider.GetRequiredService<IDocumentStorage>();
        Guid resolvedTenantId = tenantId ?? Guid.NewGuid();
        Guid knowledgeBaseId = Guid.NewGuid();
        Guid versionId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        string objectKey =
            $"{resolvedTenantId:N}/{knowledgeBaseId:N}/{versionId:N}/{documentId:N}.txt";
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(bytes, writable: false);
        StoredDocument stored = await storage.StoreAsync(
            new DocumentStorageWriteRequest(objectKey, stream, "text/plain"),
            CancellationToken.None);

        if (tenantId is null)
        {
            context.Tenants.Add(
                Tenant.Create(
                    resolvedTenantId,
                    $"processing-tenant-{resolvedTenantId:N}"));
        }

        var knowledgeBase = KnowledgeBase.Create(
            knowledgeBaseId,
            resolvedTenantId,
            $"processing-kb-{knowledgeBaseId:N}");
        KnowledgeBaseVersion version = KnowledgeBaseVersion.Create(
            versionId,
            resolvedTenantId,
            knowledgeBaseId,
            "integration-test-model",
            RagDatabaseConstants.EmbeddingDimensions);
        Document document = Document.Create(
            documentId,
            resolvedTenantId,
            knowledgeBaseId,
            versionId,
            "processing.txt",
            stored.ObjectKey,
            "text/plain",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.Length);
        IngestionJob job = IngestionJob.Create(
            jobId,
            resolvedTenantId,
            knowledgeBaseId,
            versionId,
            documentId,
            DateTimeOffset.UtcNow,
            maxAttempts: 2);
        context.AddRange(knowledgeBase, version, document, job);
        await context.SaveChangesAsync();
        return new SeededDocument(
            resolvedTenantId,
            knowledgeBaseId,
            versionId,
            documentId,
            jobId);
    }

    private static string CreateContent(int tokenCount) =>
        string.Join(
            ' ',
            Enumerable.Range(0, tokenCount).Select(index => $"token_{index}"));

    private sealed record SeededDocument(
        Guid TenantId,
        Guid KnowledgeBaseId,
        Guid VersionId,
        Guid DocumentId,
        Guid JobId);
}
