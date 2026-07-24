namespace Rag.Application.Ingestion;

public interface IDocumentUploadService
{
    ValueTask<StagedDocumentUpload> StageAsync(
        StageDocumentUploadCommand command,
        CancellationToken cancellationToken);

    ValueTask<DocumentUploadResult> CommitAsync(
        StagedDocumentUpload upload,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask DiscardAsync(
        StagedDocumentUpload upload,
        CancellationToken cancellationToken);
}

public sealed record StageDocumentUploadCommand(
    Guid TenantId,
    Guid KnowledgeBaseId,
    string FileName,
    string ContentType,
    Stream Content);

public sealed record StagedDocumentUpload(
    Guid TenantId,
    Guid KnowledgeBaseId,
    Guid VersionId,
    Guid DocumentId,
    Guid JobId,
    string FileName,
    string ContentType,
    string ObjectKey,
    string ContentHash,
    long SizeBytes);

public enum DocumentUploadOutcome
{
    Accepted,
    Replayed,
    Conflict,
    KnowledgeBaseNotFound,
}

public sealed record DocumentUploadResult(
    DocumentUploadOutcome Outcome,
    Guid? DocumentId = null,
    Guid? VersionId = null,
    Guid? JobId = null);
