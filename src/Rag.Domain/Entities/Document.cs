using Rag.Domain.Common;
using Rag.Domain.Enums;

namespace Rag.Domain.Entities;

public sealed class Document : AuditableEntity
{
    private Document(
        Guid id,
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        string fileName,
        string storageObjectKey,
        string contentType,
        string contentHash,
        long sizeBytes)
        : base(id)
    {
        TenantId = DomainGuard.Required(tenantId, nameof(tenantId));
        KnowledgeBaseId = DomainGuard.Required(knowledgeBaseId, nameof(knowledgeBaseId));
        VersionId = DomainGuard.Required(versionId, nameof(versionId));
        FileName = DomainGuard.Required(fileName, 512, nameof(fileName));
        StorageObjectKey = DomainGuard.Required(storageObjectKey, 1024, nameof(storageObjectKey));
        ContentType = DomainGuard.Required(contentType, 100, nameof(contentType));
        ContentHash = DomainGuard.Sha256(contentHash, nameof(contentHash));

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }

        SizeBytes = sizeBytes;
        Status = DocumentStatus.Uploaded;
    }

    private Document()
    {
        FileName = string.Empty;
        StorageObjectKey = string.Empty;
        ContentType = string.Empty;
        ContentHash = string.Empty;
    }

    public Guid TenantId { get; private set; }

    public Guid KnowledgeBaseId { get; private set; }

    public Guid VersionId { get; private set; }

    public string FileName { get; private set; }

    public string StorageObjectKey { get; private set; }

    public string ContentType { get; private set; }

    public string ContentHash { get; private set; }

    public long SizeBytes { get; private set; }

    public DocumentStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

    public static Document Create(
        Guid id,
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        string fileName,
        string storageObjectKey,
        string contentType,
        string contentHash,
        long sizeBytes) =>
        new(
            id,
            tenantId,
            knowledgeBaseId,
            versionId,
            fileName,
            storageObjectKey,
            contentType,
            contentHash,
            sizeBytes);

    public void MarkProcessing()
    {
        if (Status == DocumentStatus.Processing)
        {
            return;
        }

        if (Status != DocumentStatus.Uploaded)
        {
            throw new InvalidOperationException($"Cannot process a document in {Status} state.");
        }

        Status = DocumentStatus.Processing;
    }

    public void MarkIndexed()
    {
        if (Status != DocumentStatus.Processing)
        {
            throw new InvalidOperationException($"Cannot index a document in {Status} state.");
        }

        Status = DocumentStatus.Indexed;
        ErrorMessage = null;
    }

    public void MarkFailed(string errorMessage)
    {
        ErrorMessage = DomainGuard.Required(errorMessage, 2000, nameof(errorMessage));
        Status = DocumentStatus.Failed;
    }

    public void PrepareForRetry()
    {
        if (Status != DocumentStatus.Failed)
        {
            throw new InvalidOperationException($"Cannot retry a document in {Status} state.");
        }

        Status = DocumentStatus.Uploaded;
        ErrorMessage = null;
    }
}

