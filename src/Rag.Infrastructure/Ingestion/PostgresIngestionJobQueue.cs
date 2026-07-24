using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Rag.Application.Abstractions;
using Rag.Application.Providers;
using Rag.Domain.Entities;
using Rag.Domain.Enums;
using Rag.Infrastructure.Persistence;

namespace Rag.Infrastructure.Ingestion;

internal sealed class PostgresIngestionJobQueue(
    RagDbContext dbContext,
    IClock clock,
    IOptions<JobsOptions> options) : IIngestionJobQueue
{
    public async ValueTask<IngestionJobLease?> TryAcquireAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow.ToUniversalTime();
        await using IDbContextTransaction transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE ingestion_jobs
            SET status = 'DeadLetter',
                lock_token = NULL,
                locked_until = NULL,
                last_error = COALESCE(last_error, 'Lease expired after the final attempt.'),
                updated_at = {now}
            WHERE status = 'Running'
              AND locked_until <= {now}
              AND attempts >= max_attempts
            """,
            cancellationToken).ConfigureAwait(false);

        List<IngestionJob> candidates = await dbContext.IngestionJobs
            .FromSqlInterpolated(
                $"""
                SELECT job.*, job.xmin
                FROM ingestion_jobs AS job
                WHERE (
                        job.status IN ('Queued', 'Retrying')
                        AND job.available_at <= {now}
                      )
                   OR (
                        job.status = 'Running'
                        AND job.locked_until <= {now}
                        AND job.attempts < job.max_attempts
                      )
                ORDER BY job.available_at, job.created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """)
            .AsTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        IngestionJob? job = candidates.SingleOrDefault();
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        Guid lockToken = Guid.NewGuid();
        job.Acquire(
            lockToken,
            now,
            TimeSpan.FromSeconds(options.Value.LeaseDurationSeconds));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new IngestionJobLease(
            job.Id,
            job.TenantId,
            job.KnowledgeBaseId,
            job.VersionId,
            job.DocumentId,
            lockToken,
            job.Attempts,
            job.MaxAttempts,
            job.LockedUntil!.Value);
    }

    public async ValueTask CompleteAsync(
        IngestionJobLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        IngestionJob job = await GetOwnedJobAsync(lease, cancellationToken).ConfigureAwait(false);
        job.Complete(lease.LockToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask FailAsync(
        IngestionJobLease lease,
        string error,
        bool isTransient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        IngestionJob job = await GetOwnedJobAsync(lease, cancellationToken).ConfigureAwait(false);
        if (!isTransient)
        {
            job.MarkFailed(lease.LockToken, error);
        }
        else if (job.Attempts >= job.MaxAttempts)
        {
            job.MarkDeadLetter(lease.LockToken, error);
        }
        else
        {
            TimeSpan retryDelay = ExponentialBackoff.Calculate(
                job.Id,
                job.Attempts,
                TimeSpan.FromSeconds(options.Value.BaseRetryDelaySeconds),
                TimeSpan.FromSeconds(options.Value.MaxRetryDelaySeconds));
            job.ReleaseForRetry(
                lease.LockToken,
                error,
                clock.UtcNow.Add(retryDelay));
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> RetryAsync(
        Guid tenantId,
        Guid ingestionJobId,
        CancellationToken cancellationToken)
    {
        IngestionJob? job = await dbContext.IngestionJobs.SingleOrDefaultAsync(
            candidate => candidate.Id == ingestionJobId &&
                candidate.TenantId == tenantId,
            cancellationToken).ConfigureAwait(false);
        if (job is null ||
            job.Status is not (IngestionJobStatus.Failed or IngestionJobStatus.DeadLetter))
        {
            return false;
        }

        job.Retry(clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<IngestionJob> GetOwnedJobAsync(
        IngestionJobLease lease,
        CancellationToken cancellationToken)
    {
        IngestionJob? job = await dbContext.IngestionJobs.SingleOrDefaultAsync(
            candidate => candidate.Id == lease.JobId &&
                candidate.LockToken == lease.LockToken &&
                candidate.Status == IngestionJobStatus.Running,
            cancellationToken).ConfigureAwait(false);
        return job ?? throw new IngestionJobLeaseLostException(lease.JobId);
    }
}
