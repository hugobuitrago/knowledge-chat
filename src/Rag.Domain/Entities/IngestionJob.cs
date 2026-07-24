using Rag.Domain.Common;
using Rag.Domain.Enums;

namespace Rag.Domain.Entities;

public sealed class IngestionJob : AuditableEntity
{
    private IngestionJob(
        Guid id,
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        Guid documentId,
        DateTimeOffset availableAt,
        int maxAttempts)
        : base(id)
    {
        TenantId = DomainGuard.Required(tenantId, nameof(tenantId));
        KnowledgeBaseId = DomainGuard.Required(knowledgeBaseId, nameof(knowledgeBaseId));
        VersionId = DomainGuard.Required(versionId, nameof(versionId));
        DocumentId = DomainGuard.Required(documentId, nameof(documentId));

        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        AvailableAt = availableAt.ToUniversalTime();
        MaxAttempts = maxAttempts;
        Status = IngestionJobStatus.Queued;
    }

    private IngestionJob()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid KnowledgeBaseId { get; private set; }

    public Guid VersionId { get; private set; }

    public Guid DocumentId { get; private set; }

    public IngestionJobStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public int MaxAttempts { get; private set; }

    public DateTimeOffset AvailableAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public Guid? LockToken { get; private set; }

    public string? LastError { get; private set; }

    public static IngestionJob Create(
        Guid id,
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        Guid documentId,
        DateTimeOffset availableAt,
        int maxAttempts) =>
        new(
            id,
            tenantId,
            knowledgeBaseId,
            versionId,
            documentId,
            availableAt,
            maxAttempts);

    public void Acquire(
        Guid lockToken,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        DomainGuard.Required(lockToken, nameof(lockToken));
        DateTimeOffset utcNow = now.ToUniversalTime();
        bool available = Status is IngestionJobStatus.Queued or IngestionJobStatus.Retrying &&
            AvailableAt <= utcNow;
        bool expired = Status == IngestionJobStatus.Running &&
            LockedUntil <= utcNow;
        if ((!available && !expired) || Attempts >= MaxAttempts)
        {
            throw new InvalidOperationException($"Cannot acquire a job in {Status} state.");
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        Status = IngestionJobStatus.Running;
        Attempts++;
        LockToken = lockToken;
        LockedUntil = utcNow.Add(leaseDuration);
    }

    public void Complete(Guid lockToken)
    {
        EnsureLease(lockToken);
        Status = IngestionJobStatus.Completed;
        LockToken = null;
        LockedUntil = null;
        LastError = null;
    }

    public void ReleaseForRetry(
        Guid lockToken,
        string error,
        DateTimeOffset availableAt)
    {
        EnsureLease(lockToken);
        if (Attempts >= MaxAttempts)
        {
            throw new InvalidOperationException("The job has exhausted its attempts.");
        }

        Status = IngestionJobStatus.Retrying;
        AvailableAt = availableAt.ToUniversalTime();
        LastError = DomainGuard.Required(error, 2000, nameof(error));
        LockToken = null;
        LockedUntil = null;
    }

    public void MarkFailed(Guid lockToken, string error)
    {
        EnsureLease(lockToken);
        Status = IngestionJobStatus.Failed;
        LastError = DomainGuard.Required(error, 2000, nameof(error));
        LockToken = null;
        LockedUntil = null;
    }

    public void MarkDeadLetter(Guid lockToken, string error)
    {
        EnsureLease(lockToken);
        Status = IngestionJobStatus.DeadLetter;
        LastError = DomainGuard.Required(error, 2000, nameof(error));
        LockToken = null;
        LockedUntil = null;
    }

    public void Retry(DateTimeOffset availableAt)
    {
        if (Status is not (IngestionJobStatus.Failed or IngestionJobStatus.DeadLetter))
        {
            throw new InvalidOperationException($"Cannot retry a job in {Status} state.");
        }

        Status = IngestionJobStatus.Queued;
        Attempts = 0;
        AvailableAt = availableAt.ToUniversalTime();
        LockToken = null;
        LockedUntil = null;
        LastError = null;
    }

    private void EnsureLease(Guid lockToken)
    {
        DomainGuard.Required(lockToken, nameof(lockToken));
        if (Status != IngestionJobStatus.Running || LockToken != lockToken)
        {
            throw new InvalidOperationException("The ingestion job lease is no longer owned.");
        }
    }
}

