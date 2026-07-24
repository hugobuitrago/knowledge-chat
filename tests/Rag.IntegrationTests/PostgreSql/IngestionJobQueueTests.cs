using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rag.Application.Providers;
using Rag.Domain.Entities;
using Rag.Domain.Enums;
using Rag.Infrastructure.Ingestion;
using Rag.Infrastructure.Persistence;

namespace Rag.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class IngestionJobQueueTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task Two_workers_cannot_acquire_the_same_job()
    {
        await ResetQueueAndSeedJobAsync(maxAttempts: 3);
        await using AsyncServiceScope firstScope = database.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = database.Services.CreateAsyncScope();
        IIngestionJobQueue firstWorker =
            firstScope.ServiceProvider.GetRequiredService<IIngestionJobQueue>();
        IIngestionJobQueue secondWorker =
            secondScope.ServiceProvider.GetRequiredService<IIngestionJobQueue>();

        Task<IngestionJobLease?> firstAcquire = firstWorker
            .TryAcquireAsync(CancellationToken.None)
            .AsTask();
        Task<IngestionJobLease?> secondAcquire = secondWorker
            .TryAcquireAsync(CancellationToken.None)
            .AsTask();
        IngestionJobLease?[] leases = await Task.WhenAll(firstAcquire, secondAcquire);

        IngestionJobLease lease = Assert.Single(leases.OfType<IngestionJobLease>());
        Assert.Equal(1, lease.Attempt);
        Assert.Single(leases, candidate => candidate is null);
    }

    [Fact]
    public async Task Expired_lease_is_recovered_by_another_worker()
    {
        Guid jobId = await ResetQueueAndSeedJobAsync(maxAttempts: 3);
        IngestionJobLease firstLease;
        await using (AsyncServiceScope firstScope = database.Services.CreateAsyncScope())
        {
            IIngestionJobQueue firstWorker =
                firstScope.ServiceProvider.GetRequiredService<IIngestionJobQueue>();
            firstLease = (await firstWorker.TryAcquireAsync(CancellationToken.None))!;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        await using AsyncServiceScope secondScope = database.Services.CreateAsyncScope();
        IIngestionJobQueue secondWorker =
            secondScope.ServiceProvider.GetRequiredService<IIngestionJobQueue>();
        IngestionJobLease? recovered = await secondWorker.TryAcquireAsync(
            CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(jobId, recovered.JobId);
        Assert.NotEqual(firstLease.LockToken, recovered.LockToken);
        Assert.Equal(2, recovered.Attempt);
    }

    [Fact]
    public async Task Renewed_lease_cannot_be_acquired_after_its_original_expiration()
    {
        await ResetQueueAndSeedJobAsync(maxAttempts: 3);
        IngestionJobLease renewed;
        await using (AsyncServiceScope firstScope = database.Services.CreateAsyncScope())
        {
            IIngestionJobQueue firstWorker =
                firstScope.ServiceProvider.GetRequiredService<IIngestionJobQueue>();
            IngestionJobLease acquired = (await firstWorker.TryAcquireAsync(
                CancellationToken.None))!;
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            renewed = await firstWorker.RenewAsync(
                acquired,
                CancellationToken.None);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(600));

        await using AsyncServiceScope secondScope = database.Services.CreateAsyncScope();
        IIngestionJobQueue secondWorker =
            secondScope.ServiceProvider.GetRequiredService<IIngestionJobQueue>();
        IngestionJobLease? competing = await secondWorker.TryAcquireAsync(
            CancellationToken.None);
        Assert.Null(competing);
        Assert.True(renewed.LockedUntil > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Transient_failures_retry_with_backoff_then_move_to_dead_letter()
    {
        Guid jobId = await ResetQueueAndSeedJobAsync(maxAttempts: 2);
        await using (AsyncServiceScope firstScope = database.Services.CreateAsyncScope())
        {
            IIngestionJobQueue queue =
                firstScope.ServiceProvider.GetRequiredService<IIngestionJobQueue>();
            IngestionJobLease first = (await queue.TryAcquireAsync(CancellationToken.None))!;
            await queue.FailAsync(
                first,
                "temporary provider failure",
                isTransient: true,
                CancellationToken.None);
        }

        await using (AsyncServiceScope inspectionScope = database.Services.CreateAsyncScope())
        {
            RagDbContext dbContext =
                inspectionScope.ServiceProvider.GetRequiredService<RagDbContext>();
            IngestionJob retrying = await dbContext.IngestionJobs
                .AsNoTracking()
                .SingleAsync(job => job.Id == jobId);
            Assert.Equal(IngestionJobStatus.Retrying, retrying.Status);
            Assert.True(retrying.AvailableAt > DateTimeOffset.UtcNow.AddSeconds(-1));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1300));

        await using (AsyncServiceScope secondScope = database.Services.CreateAsyncScope())
        {
            IIngestionJobQueue queue =
                secondScope.ServiceProvider.GetRequiredService<IIngestionJobQueue>();
            IngestionJobLease second = (await queue.TryAcquireAsync(CancellationToken.None))!;
            Assert.Equal(2, second.Attempt);
            await queue.FailAsync(
                second,
                "temporary provider failure",
                isTransient: true,
                CancellationToken.None);
        }

        await using AsyncServiceScope finalScope = database.Services.CreateAsyncScope();
        RagDbContext finalContext =
            finalScope.ServiceProvider.GetRequiredService<RagDbContext>();
        IngestionJob deadLetter = await finalContext.IngestionJobs
            .AsNoTracking()
            .SingleAsync(job => job.Id == jobId);
        Document failedDocument = await finalContext.Documents
            .AsNoTracking()
            .SingleAsync(document => document.Id == deadLetter.DocumentId);
        KnowledgeBaseVersion failedVersion = await finalContext.KnowledgeBaseVersions
            .AsNoTracking()
            .SingleAsync(version => version.Id == deadLetter.VersionId);
        Assert.Equal(IngestionJobStatus.DeadLetter, deadLetter.Status);
        Assert.Null(deadLetter.LockToken);
        Assert.Equal(DocumentStatus.Failed, failedDocument.Status);
        Assert.Equal(KnowledgeBaseVersionStatus.Failed, failedVersion.Status);

        IIngestionJobQueue retryQueue =
            finalScope.ServiceProvider.GetRequiredService<IIngestionJobQueue>();
        Assert.True(await retryQueue.RetryAsync(
            deadLetter.TenantId,
            jobId,
            CancellationToken.None));
        finalContext.ChangeTracker.Clear();
        IngestionJob retried = await finalContext.IngestionJobs
            .AsNoTracking()
            .SingleAsync(job => job.Id == jobId);
        Document resetDocument = await finalContext.Documents
            .AsNoTracking()
            .SingleAsync(document => document.Id == retried.DocumentId);
        KnowledgeBaseVersion resetVersion = await finalContext.KnowledgeBaseVersions
            .AsNoTracking()
            .SingleAsync(version => version.Id == retried.VersionId);
        Assert.Equal(IngestionJobStatus.Queued, retried.Status);
        Assert.Equal(0, retried.Attempts);
        Assert.Equal(DocumentStatus.Uploaded, resetDocument.Status);
        Assert.Equal(KnowledgeBaseVersionStatus.Pending, resetVersion.Status);
    }

    [Fact]
    public void Exponential_backoff_is_bounded_and_deterministic()
    {
        Guid jobId = Guid.NewGuid();
        TimeSpan first = ExponentialBackoff.Calculate(
            jobId,
            attempt: 1,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(1));
        TimeSpan repeated = ExponentialBackoff.Calculate(
            jobId,
            attempt: 1,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(1));
        TimeSpan capped = ExponentialBackoff.Calculate(
            jobId,
            attempt: 20,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(1));

        Assert.Equal(first, repeated);
        Assert.InRange(first, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12));
        Assert.InRange(capped, TimeSpan.FromSeconds(48), TimeSpan.FromMinutes(1));
    }

    private async Task<Guid> ResetQueueAndSeedJobAsync(int maxAttempts)
    {
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        await dbContext.IngestionJobs.ExecuteDeleteAsync();

        Guid tenantId = Guid.NewGuid();
        Guid knowledgeBaseId = Guid.NewGuid();
        Guid versionId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        var tenant = Tenant.Create(tenantId, $"queue-tenant-{tenantId:N}");
        KnowledgeBase knowledgeBase = KnowledgeBase.Create(
            knowledgeBaseId,
            tenantId,
            $"queue-kb-{knowledgeBaseId:N}");
        KnowledgeBaseVersion version = KnowledgeBaseVersion.Create(
            versionId,
            tenantId,
            knowledgeBaseId,
            "integration-test-model",
            1536);
        Document document = Document.Create(
            documentId,
            tenantId,
            knowledgeBaseId,
            versionId,
            "queue.txt",
            $"queue/{documentId:N}.txt",
            "text/plain",
            new string('b', 64),
            1);
        IngestionJob job = IngestionJob.Create(
            jobId,
            tenantId,
            knowledgeBaseId,
            versionId,
            documentId,
            DateTimeOffset.UtcNow,
            maxAttempts);
        dbContext.AddRange(tenant, knowledgeBase, version, document, job);
        await dbContext.SaveChangesAsync();
        return jobId;
    }
}
