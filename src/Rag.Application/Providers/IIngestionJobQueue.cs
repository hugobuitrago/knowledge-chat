namespace Rag.Application.Providers;

public interface IIngestionJobQueue
{
    ValueTask<IngestionJobLease?> TryAcquireAsync(
        CancellationToken cancellationToken);

    ValueTask<IngestionJobLease> RenewAsync(
        IngestionJobLease lease,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(
        IngestionJobLease lease,
        CancellationToken cancellationToken);

    ValueTask FailAsync(
        IngestionJobLease lease,
        string error,
        bool isTransient,
        CancellationToken cancellationToken);

    ValueTask<bool> RetryAsync(
        Guid tenantId,
        Guid ingestionJobId,
        CancellationToken cancellationToken);
}

public sealed record IngestionJobLease(
    Guid JobId,
    Guid TenantId,
    Guid KnowledgeBaseId,
    Guid VersionId,
    Guid DocumentId,
    Guid LockToken,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset LockedUntil);

