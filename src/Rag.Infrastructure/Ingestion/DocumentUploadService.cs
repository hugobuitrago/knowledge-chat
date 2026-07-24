using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Rag.Application.Abstractions;
using Rag.Application.Ingestion;
using Rag.Application.Providers;
using Rag.Contracts.Ingestions;
using Rag.Domain.Entities;
using Rag.Infrastructure.Persistence;

namespace Rag.Infrastructure.Ingestion;

internal sealed class DocumentUploadService(
    RagDbContext dbContext,
    IDocumentStorage documentStorage,
    IClock clock,
    IOptions<UploadsOptions> uploadOptions,
    IOptions<JobsOptions> jobOptions,
    IOptions<EmbeddingMetadataOptions> embeddingOptions,
    ILogger<DocumentUploadService> logger) : IDocumentUploadService
{
    private const string Operation = "upload-document";
    private const int AcceptedStatusCode = 202;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async ValueTask<StagedDocumentUpload> StageAsync(
        StageDocumentUploadCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Guid versionId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        string objectKey =
            $"{command.TenantId:N}/{command.KnowledgeBaseId:N}/{versionId:N}/{documentId:N}.txt";
        await using var validatedContent = new ValidatedTextUploadStream(
            command.Content,
            uploadOptions.Value.MaxFileSizeBytes);
        StoredDocument storedDocument = await documentStorage.StoreAsync(
            new DocumentStorageWriteRequest(
                objectKey,
                validatedContent,
                command.ContentType),
            cancellationToken).ConfigureAwait(false);

        return new StagedDocumentUpload(
            command.TenantId,
            command.KnowledgeBaseId,
            versionId,
            documentId,
            jobId,
            command.FileName,
            command.ContentType,
            storedDocument.ObjectKey,
            storedDocument.ContentHash,
            storedDocument.Length);
    }

    public async ValueTask<DocumentUploadResult> CommitAsync(
        StagedDocumentUpload upload,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        string requestHash = CalculateRequestHash(upload);
        await using IDbContextTransaction transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            IdempotencyRecord? existingRecord = await dbContext.IdempotencyRecords
                .SingleOrDefaultAsync(
                    record => record.TenantId == upload.TenantId &&
                        record.Operation == Operation &&
                        record.Key == idempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existingRecord is not null && existingRecord.ExpiresAt <= clock.UtcNow)
            {
                dbContext.IdempotencyRecords.Remove(existingRecord);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                existingRecord = null;
            }

            if (existingRecord is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return ResolveExisting(existingRecord, requestHash);
            }

            bool knowledgeBaseExists = await dbContext.KnowledgeBases
                .AsNoTracking()
                .AnyAsync(
                    knowledgeBase => knowledgeBase.Id == upload.KnowledgeBaseId &&
                        knowledgeBase.TenantId == upload.TenantId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!knowledgeBaseExists)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new DocumentUploadResult(
                    DocumentUploadOutcome.KnowledgeBaseNotFound);
            }

            DateTimeOffset now = clock.UtcNow;
            var version = KnowledgeBaseVersion.Create(
                upload.VersionId,
                upload.TenantId,
                upload.KnowledgeBaseId,
                embeddingOptions.Value.Model,
                embeddingOptions.Value.Dimensions);
            var document = Document.Create(
                upload.DocumentId,
                upload.TenantId,
                upload.KnowledgeBaseId,
                upload.VersionId,
                upload.FileName,
                upload.ObjectKey,
                upload.ContentType,
                upload.ContentHash,
                upload.SizeBytes);
            var job = IngestionJob.Create(
                upload.JobId,
                upload.TenantId,
                upload.KnowledgeBaseId,
                upload.VersionId,
                upload.DocumentId,
                now,
                jobOptions.Value.MaxAttempts);
            var response = new UploadAcceptedResponse(
                upload.DocumentId,
                upload.VersionId,
                upload.JobId,
                $"/v1/ingestions/{upload.JobId:D}");
            var idempotencyRecord = IdempotencyRecord.Create(
                Guid.NewGuid(),
                upload.TenantId,
                idempotencyKey,
                Operation,
                requestHash,
                now.AddHours(uploadOptions.Value.IdempotencyTtlHours));
            idempotencyRecord.StoreResponse(
                AcceptedStatusCode,
                JsonSerializer.Serialize(response, JsonOptions));

            dbContext.AddRange(version, document, job, idempotencyRecord);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new DocumentUploadResult(
                DocumentUploadOutcome.Accepted,
                upload.DocumentId,
                upload.VersionId,
                upload.JobId);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
            })
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            IdempotencyRecord? concurrentRecord = await dbContext.IdempotencyRecords
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.TenantId == upload.TenantId &&
                        record.Operation == Operation &&
                        record.Key == idempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (concurrentRecord is null)
            {
                throw;
            }

            return ResolveExisting(concurrentRecord, requestHash);
        }
    }

    public async ValueTask DiscardAsync(
        StagedDocumentUpload upload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);

        try
        {
            await documentStorage
                .DeleteAsync(upload.ObjectKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            logger.LogWarning(
                exception,
                "Could not remove staged document {DocumentId}.",
                upload.DocumentId);
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(
                exception,
                "Could not remove staged document {DocumentId}.",
                upload.DocumentId);
        }
    }

    private static DocumentUploadResult ResolveExisting(
        IdempotencyRecord record,
        string requestHash)
    {
        if (!string.Equals(record.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return new DocumentUploadResult(DocumentUploadOutcome.Conflict);
        }

        if (record.ResponseStatusCode != AcceptedStatusCode ||
            string.IsNullOrWhiteSpace(record.ResponseBodyJson))
        {
            return new DocumentUploadResult(DocumentUploadOutcome.Conflict);
        }

        UploadAcceptedResponse? response = JsonSerializer.Deserialize<UploadAcceptedResponse>(
            record.ResponseBodyJson,
            JsonOptions);
        return response is null
            ? new DocumentUploadResult(DocumentUploadOutcome.Conflict)
            : new DocumentUploadResult(
                DocumentUploadOutcome.Replayed,
                response.DocumentId,
                response.VersionId,
                response.JobId);
    }

    private static string CalculateRequestHash(StagedDocumentUpload upload)
    {
        string canonicalRequest = string.Join(
            '\n',
            upload.KnowledgeBaseId.ToString("D"),
            upload.FileName,
            upload.ContentType,
            upload.ContentHash);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))
            .ToLowerInvariant();
    }
}
