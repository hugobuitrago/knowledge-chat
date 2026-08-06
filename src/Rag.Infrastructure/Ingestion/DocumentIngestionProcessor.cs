using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Rag.Application.Abstractions;
using Rag.Application.Ingestion;
using Rag.Application.Providers;
using Rag.Domain.Entities;
using Rag.Domain.Enums;
using Rag.Infrastructure.Persistence;

namespace Rag.Infrastructure.Ingestion;

internal sealed class DocumentIngestionProcessor(
    RagDbContext dbContext,
    IDocumentStorage documentStorage,
    ITextChunker textChunker,
    IIngestionJobQueue jobQueue,
    IKnowledgeBaseVersionActivator versionActivator,
    EmbeddingBatchExecutor embeddingExecutor,
    IClock clock,
    IOptions<EmbeddingMetadataOptions> embeddingOptions) :
    IDocumentIngestionProcessor
{
    public async ValueTask ProcessAsync(
        IngestionJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        IngestionJobLease currentLease = await jobQueue
            .RenewAsync(lease, cancellationToken)
            .ConfigureAwait(false);
        ProcessingSource source = await LoadAndMarkProcessingAsync(
            currentLease,
            cancellationToken).ConfigureAwait(false);
        string sourceText = await ReadSourceAsync(
            source.StorageObjectKey,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TextChunk> chunks = textChunker.Chunk(sourceText);
        if (chunks.Count == 0)
        {
            throw new IngestionProcessingException(
                "The normalized document does not contain indexable text.",
                isTransient: false);
        }

        Dictionary<string, float[]> embeddings = await LoadReusableEmbeddingsAsync(
            currentLease.TenantId,
            source.EmbeddingModel,
            source.EmbeddingDimensions,
            chunks,
            cancellationToken).ConfigureAwait(false);
        string[] missingHashes = chunks
            .Select(static chunk => chunk.ContentHash)
            .Distinct(StringComparer.Ordinal)
            .Where(hash => !embeddings.ContainsKey(hash))
            .ToArray();
        Dictionary<string, string> contentByHash = chunks
            .GroupBy(static chunk => chunk.ContentHash, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Content,
                StringComparer.Ordinal);

        for (int offset = 0;
             offset < missingHashes.Length;
             offset += embeddingOptions.Value.BatchSize)
        {
            currentLease = await jobQueue
                .RenewAsync(currentLease, cancellationToken)
                .ConfigureAwait(false);
            string[] batchHashes = missingHashes
                .Skip(offset)
                .Take(embeddingOptions.Value.BatchSize)
                .ToArray();
            string[] batchInputs = batchHashes
                .Select(hash => contentByHash[hash])
                .ToArray();
            EmbeddingBatch batch = await embeddingExecutor
                .GenerateAsync(batchInputs, cancellationToken)
                .ConfigureAwait(false);
            ValidateBatch(batch, batchInputs.Length, source);
            for (int index = 0; index < batchHashes.Length; index++)
            {
                embeddings.Add(batchHashes[index], batch.Vectors[index].ToArray());
            }
        }

        currentLease = await jobQueue
            .RenewAsync(currentLease, cancellationToken)
            .ConfigureAwait(false);
        await PersistAsync(
            currentLease,
            chunks,
            embeddings,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessingSource> LoadAndMarkProcessingAsync(
        IngestionJobLease lease,
        CancellationToken cancellationToken)
    {
        Document? document = await dbContext.Documents.SingleOrDefaultAsync(
            candidate => candidate.Id == lease.DocumentId &&
                candidate.TenantId == lease.TenantId &&
                candidate.KnowledgeBaseId == lease.KnowledgeBaseId &&
                candidate.VersionId == lease.VersionId,
            cancellationToken).ConfigureAwait(false);
        KnowledgeBaseVersion? version =
            await dbContext.KnowledgeBaseVersions.SingleOrDefaultAsync(
                candidate => candidate.Id == lease.VersionId &&
                    candidate.TenantId == lease.TenantId &&
                    candidate.KnowledgeBaseId == lease.KnowledgeBaseId,
                cancellationToken).ConfigureAwait(false);
        if (document is null || version is null)
        {
            throw new IngestionProcessingException(
                "The ingestion job references missing processing records.",
                isTransient: false);
        }

        if (document.Status == DocumentStatus.Uploaded)
        {
            document.MarkProcessing();
        }
        else if (document.Status is not (DocumentStatus.Processing or DocumentStatus.Indexed))
        {
            throw new IngestionProcessingException(
                $"The document cannot be processed from state {document.Status}.",
                isTransient: false);
        }

        if (version.Status == KnowledgeBaseVersionStatus.Pending)
        {
            version.MarkProcessing();
        }
        else if (version.Status is not (
                     KnowledgeBaseVersionStatus.Processing or
                     KnowledgeBaseVersionStatus.Ready))
        {
            throw new IngestionProcessingException(
                $"The version cannot be processed from state {version.Status}.",
                isTransient: false);
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ProcessingSource(
            document.StorageObjectKey,
            version.EmbeddingModel,
            version.EmbeddingDimensions);
    }

    private async Task<string> ReadSourceAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await using Stream stream = await documentStorage
                .OpenReadAsync(objectKey, cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 64 * 1024,
                leaveOpen: false);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException exception)
        {
            throw new IngestionProcessingException(
                "The stored document is not valid UTF-8.",
                isTransient: false,
                exception);
        }
    }

    private async Task<Dictionary<string, float[]>> LoadReusableEmbeddingsAsync(
        Guid tenantId,
        string model,
        int dimensions,
        IReadOnlyList<TextChunk> chunks,
        CancellationToken cancellationToken)
    {
        string[] hashes = chunks
            .Select(static chunk => chunk.ContentHash)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        List<ReusableEmbedding> candidates = await (
                from chunk in dbContext.DocumentChunks.AsNoTracking()
                join version in dbContext.KnowledgeBaseVersions.AsNoTracking()
                    on new
                    {
                        chunk.TenantId,
                        chunk.KnowledgeBaseId,
                        chunk.VersionId,
                    }
                    equals new
                    {
                        version.TenantId,
                        version.KnowledgeBaseId,
                        VersionId = version.Id,
                    }
                where chunk.TenantId == tenantId &&
                    hashes.Contains(chunk.ContentHash) &&
                    chunk.ChunkingConfigurationHash == textChunker.ConfigurationHash &&
                    version.EmbeddingModel == model &&
                    version.EmbeddingDimensions == dimensions
                select new ReusableEmbedding(
                    chunk.ContentHash,
                    chunk.Embedding))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return candidates
            .GroupBy(static candidate => candidate.ContentHash, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Embedding.ToArray(),
                StringComparer.Ordinal);
    }

    private void ValidateBatch(
        EmbeddingBatch batch,
        int expectedCount,
        ProcessingSource source)
    {
        if (!string.Equals(batch.Model, source.EmbeddingModel, StringComparison.Ordinal) ||
            batch.Dimensions != source.EmbeddingDimensions ||
            batch.Vectors.Count != expectedCount ||
            batch.Vectors.Any(vector => vector.Length != source.EmbeddingDimensions))
        {
            throw new EmbeddingProviderException(
                "The embedding provider returned an incompatible batch.",
                isTransient: false);
        }
    }

    private async Task PersistAsync(
        IngestionJobLease lease,
        IReadOnlyList<TextChunk> chunks,
        IReadOnlyDictionary<string, float[]> embeddings,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using IDbContextTransaction transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        List<IngestionJob> jobs = await dbContext.IngestionJobs
            .FromSqlInterpolated(
                $"""
                SELECT job.*, job.xmin
                FROM ingestion_jobs AS job
                WHERE job.id = {lease.JobId}
                  AND job.tenant_id = {lease.TenantId}
                  AND job.lock_token = {lease.LockToken}
                  AND job.status = 'Running'
                FOR UPDATE
                """)
            .AsTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        IngestionJob? job = jobs.SingleOrDefault();
        if (job is null || job.LockedUntil <= clock.UtcNow)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new IngestionJobLeaseLostException(lease.JobId);
        }

        Document document = await dbContext.Documents.SingleAsync(
            candidate => candidate.Id == lease.DocumentId &&
                candidate.TenantId == lease.TenantId &&
                candidate.KnowledgeBaseId == lease.KnowledgeBaseId &&
                candidate.VersionId == lease.VersionId,
            cancellationToken).ConfigureAwait(false);
        KnowledgeBaseVersion version = await dbContext.KnowledgeBaseVersions.SingleAsync(
            candidate => candidate.Id == lease.VersionId &&
                candidate.TenantId == lease.TenantId &&
                candidate.KnowledgeBaseId == lease.KnowledgeBaseId,
            cancellationToken).ConfigureAwait(false);

        await dbContext.DocumentChunks
            .Where(chunk => chunk.TenantId == lease.TenantId &&
                chunk.KnowledgeBaseId == lease.KnowledgeBaseId &&
                chunk.VersionId == lease.VersionId &&
                chunk.DocumentId == lease.DocumentId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        dbContext.DocumentChunks.AddRange(
            chunks.Select(chunk =>
                DocumentChunk.Create(
                    CreateChunkId(lease.DocumentId, chunk),
                    lease.TenantId,
                    lease.KnowledgeBaseId,
                    lease.VersionId,
                    lease.DocumentId,
                    chunk.Index,
                    chunk.Content,
                    chunk.ContentHash,
                    chunk.TokenCount,
                    chunk.StartOffset,
                    chunk.EndOffset,
                    textChunker.ConfigurationHash,
                    embeddings[chunk.ContentHash],
                    "{}")));

        if (document.Status == DocumentStatus.Processing)
        {
            document.MarkIndexed();
        }

        bool hasOtherIncompleteDocuments = await dbContext.Documents
            .AsNoTracking()
            .AnyAsync(
                candidate => candidate.TenantId == lease.TenantId &&
                    candidate.KnowledgeBaseId == lease.KnowledgeBaseId &&
                    candidate.VersionId == lease.VersionId &&
                    candidate.Id != lease.DocumentId &&
                    candidate.Status != DocumentStatus.Indexed,
                cancellationToken)
            .ConfigureAwait(false);
        if (!hasOtherIncompleteDocuments &&
            version.Status == KnowledgeBaseVersionStatus.Processing)
        {
            version.MarkReady();
        }

        job.Complete(lease.LockToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await versionActivator
            .ActivateAsync(
                lease.TenantId,
                version.KnowledgeBaseId,
                version.Id,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Guid CreateChunkId(Guid documentId, TextChunk chunk)
    {
        byte[] source = Encoding.UTF8.GetBytes(
            $"{documentId:N}|{chunk.Index}|{chunk.ContentHash}");
        byte[] hash = SHA256.HashData(source);
        Span<byte> guidBytes = hash.AsSpan(0, 16);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    private sealed record ProcessingSource(
        string StorageObjectKey,
        string EmbeddingModel,
        int EmbeddingDimensions);

    private sealed record ReusableEmbedding(
        string ContentHash,
        float[] Embedding);
}
