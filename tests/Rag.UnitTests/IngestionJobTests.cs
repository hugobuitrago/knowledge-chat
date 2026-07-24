using Rag.Domain.Entities;
using Rag.Domain.Enums;

namespace Rag.UnitTests;

public sealed class IngestionJobTests
{
    [Fact]
    public void Expired_lease_can_be_reacquired_with_a_new_attempt()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IngestionJob job = CreateJob(now, maxAttempts: 3);
        Guid firstToken = Guid.NewGuid();
        job.Acquire(firstToken, now, TimeSpan.FromSeconds(10));

        Guid secondToken = Guid.NewGuid();
        job.Acquire(secondToken, now.AddSeconds(11), TimeSpan.FromSeconds(10));

        Assert.Equal(IngestionJobStatus.Running, job.Status);
        Assert.Equal(2, job.Attempts);
        Assert.Equal(secondToken, job.LockToken);
    }

    [Fact]
    public void Transient_failure_releases_lease_for_retry()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IngestionJob job = CreateJob(now, maxAttempts: 3);
        Guid token = Guid.NewGuid();
        job.Acquire(token, now, TimeSpan.FromMinutes(1));

        job.ReleaseForRetry(token, "temporary failure", now.AddSeconds(5));

        Assert.Equal(IngestionJobStatus.Retrying, job.Status);
        Assert.Null(job.LockToken);
        Assert.Null(job.LockedUntil);
        Assert.Equal("temporary failure", job.LastError);
    }

    [Fact]
    public void Only_the_current_lease_owner_can_complete_a_job()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IngestionJob job = CreateJob(now, maxAttempts: 3);
        job.Acquire(Guid.NewGuid(), now, TimeSpan.FromMinutes(1));

        Assert.Throws<InvalidOperationException>(() => job.Complete(Guid.NewGuid()));
        Assert.Equal(IngestionJobStatus.Running, job.Status);
    }

    [Fact]
    public void Manual_retry_resets_a_dead_letter_job()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IngestionJob job = CreateJob(now, maxAttempts: 1);
        Guid token = Guid.NewGuid();
        job.Acquire(token, now, TimeSpan.FromMinutes(1));
        job.MarkDeadLetter(token, "attempts exhausted");

        job.Retry(now.AddMinutes(1));

        Assert.Equal(IngestionJobStatus.Queued, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Null(job.LastError);
    }

    [Fact]
    public void Current_owner_can_renew_a_non_expired_lease()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IngestionJob job = CreateJob(now, maxAttempts: 2);
        Guid token = Guid.NewGuid();
        job.Acquire(token, now, TimeSpan.FromSeconds(10));

        job.Renew(token, now.AddSeconds(5), TimeSpan.FromSeconds(10));

        Assert.Equal(now.AddSeconds(15), job.LockedUntil);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public void Expired_lease_cannot_be_renewed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IngestionJob job = CreateJob(now, maxAttempts: 2);
        Guid token = Guid.NewGuid();
        job.Acquire(token, now, TimeSpan.FromSeconds(10));

        Assert.Throws<InvalidOperationException>(
            () => job.Renew(
                token,
                now.AddSeconds(11),
                TimeSpan.FromSeconds(10)));
    }

    private static IngestionJob CreateJob(DateTimeOffset now, int maxAttempts) =>
        IngestionJob.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            now,
            maxAttempts);
}
