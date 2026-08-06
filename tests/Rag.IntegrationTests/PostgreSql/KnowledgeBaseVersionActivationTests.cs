using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rag.Application.Ingestion;
using Rag.Domain.Entities;
using Rag.Domain.Enums;
using Rag.Infrastructure.Persistence;

namespace Rag.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class KnowledgeBaseVersionActivationTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task Activation_archives_previous_version_and_is_idempotent()
    {
        VersionScenario scenario = await SeedScenarioAsync();

        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        IKnowledgeBaseVersionActivator activator = scope.ServiceProvider
            .GetRequiredService<IKnowledgeBaseVersionActivator>();
        VersionActivationResult result = await activator.ActivateAsync(
            scenario.TenantId,
            scenario.KnowledgeBaseId,
            scenario.TargetVersionId,
            CancellationToken.None);
        VersionActivationResult repeated = await activator.ActivateAsync(
            scenario.TenantId,
            scenario.KnowledgeBaseId,
            scenario.TargetVersionId,
            CancellationToken.None);

        Assert.Equal(scenario.PreviousVersionId, result.ArchivedVersionId);
        Assert.False(result.AlreadyActive);
        Assert.True(repeated.AlreadyActive);
        Assert.Null(repeated.ArchivedVersionId);

        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        Dictionary<Guid, KnowledgeBaseVersionStatus> statuses = await context
            .KnowledgeBaseVersions
            .AsNoTracking()
            .Where(version => version.KnowledgeBaseId == scenario.KnowledgeBaseId)
            .ToDictionaryAsync(version => version.Id, version => version.Status);
        Assert.Equal(
            KnowledgeBaseVersionStatus.Archived,
            statuses[scenario.PreviousVersionId]);
        Assert.Equal(
            KnowledgeBaseVersionStatus.Active,
            statuses[scenario.TargetVersionId]);
    }

    [Fact]
    public async Task Validation_failure_preserves_previous_active_version()
    {
        VersionScenario scenario = await SeedScenarioAsync(targetHasChunk: false);

        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        IKnowledgeBaseVersionActivator activator = scope.ServiceProvider
            .GetRequiredService<IKnowledgeBaseVersionActivator>();
        VersionActivationException exception = await Assert.ThrowsAsync<VersionActivationException>(
            () => activator.ActivateAsync(
                scenario.TenantId,
                scenario.KnowledgeBaseId,
                scenario.TargetVersionId,
                CancellationToken.None).AsTask());

        Assert.Contains("chunk", exception.Message, StringComparison.OrdinalIgnoreCase);
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        KnowledgeBaseVersion active = await context.KnowledgeBaseVersions
            .AsNoTracking()
            .SingleAsync(version =>
                version.KnowledgeBaseId == scenario.KnowledgeBaseId &&
                version.Status == KnowledgeBaseVersionStatus.Active);
        Assert.Equal(scenario.PreviousVersionId, active.Id);
    }

    [Fact]
    public async Task Database_failure_before_commit_rolls_back_previous_archive()
    {
        VersionScenario scenario = await SeedScenarioAsync();
        await using (AsyncServiceScope setupScope = database.Services.CreateAsyncScope())
        {
            RagDbContext setup = setupScope.ServiceProvider.GetRequiredService<RagDbContext>();
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION test_reject_version_activation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF OLD.status <> 'Active' AND NEW.status = 'Active' THEN
                        RAISE EXCEPTION 'Activation rejected by integration test.'
                            USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER test_reject_version_activation
                BEFORE UPDATE OF status ON knowledge_base_versions
                FOR EACH ROW EXECUTE FUNCTION test_reject_version_activation();
                """);
        }

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => ActivateInNewScopeAsync(
                    scenario.TenantId,
                    scenario.KnowledgeBaseId,
                    scenario.TargetVersionId));
        }
        finally
        {
            await using AsyncServiceScope teardownScope = database.Services.CreateAsyncScope();
            RagDbContext teardown = teardownScope.ServiceProvider
                .GetRequiredService<RagDbContext>();
            await teardown.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER IF EXISTS test_reject_version_activation
                    ON knowledge_base_versions;
                DROP FUNCTION IF EXISTS test_reject_version_activation();
                """);
        }

        await using AsyncServiceScope inspectionScope = database.Services.CreateAsyncScope();
        RagDbContext inspection = inspectionScope.ServiceProvider
            .GetRequiredService<RagDbContext>();
        KnowledgeBaseVersion active = await inspection.KnowledgeBaseVersions
            .AsNoTracking()
            .SingleAsync(version =>
                version.KnowledgeBaseId == scenario.KnowledgeBaseId &&
                version.Status == KnowledgeBaseVersionStatus.Active);
        Assert.Equal(scenario.PreviousVersionId, active.Id);
    }

    [Fact]
    public async Task Readers_observe_old_version_until_activation_transaction_commits()
    {
        VersionScenario scenario = await SeedScenarioAsync();
        await using AsyncServiceScope writerScope = database.Services.CreateAsyncScope();
        await using AsyncServiceScope readerScope = database.Services.CreateAsyncScope();
        RagDbContext writer = writerScope.ServiceProvider.GetRequiredService<RagDbContext>();
        RagDbContext reader = readerScope.ServiceProvider.GetRequiredService<RagDbContext>();
        await using IDbContextTransaction transaction = await writer.Database
            .BeginTransactionAsync();

        KnowledgeBaseVersion previous = await writer.KnowledgeBaseVersions
            .SingleAsync(version => version.Id == scenario.PreviousVersionId);
        KnowledgeBaseVersion target = await writer.KnowledgeBaseVersions
            .SingleAsync(version => version.Id == scenario.TargetVersionId);
        previous.Archive();
        await writer.SaveChangesAsync();
        target.Activate();
        await writer.SaveChangesAsync();

        Guid visibleBeforeCommit = await reader.KnowledgeBaseVersions
            .AsNoTracking()
            .Where(version =>
                version.KnowledgeBaseId == scenario.KnowledgeBaseId &&
                version.Status == KnowledgeBaseVersionStatus.Active)
            .Select(version => version.Id)
            .SingleAsync();
        Assert.Equal(scenario.PreviousVersionId, visibleBeforeCommit);

        await transaction.CommitAsync();
        Guid visibleAfterCommit = await reader.KnowledgeBaseVersions
            .AsNoTracking()
            .Where(version =>
                version.KnowledgeBaseId == scenario.KnowledgeBaseId &&
                version.Status == KnowledgeBaseVersionStatus.Active)
            .Select(version => version.Id)
            .SingleAsync();
        Assert.Equal(scenario.TargetVersionId, visibleAfterCommit);
    }

    [Fact]
    public async Task Concurrent_activations_leave_exactly_one_active_version()
    {
        VersionScenario scenario = await SeedScenarioAsync();
        Guid competingVersionId;
        await using (AsyncServiceScope seedScope = database.Services.CreateAsyncScope())
        {
            RagDbContext context = seedScope.ServiceProvider.GetRequiredService<RagDbContext>();
            VersionSeed competing = await AddReadyVersionAsync(
                context,
                scenario.TenantId,
                scenario.KnowledgeBaseId,
                hasChunk: true);
            competingVersionId = competing.VersionId;
        }

        Task first = ActivateInNewScopeAsync(
            scenario.TenantId,
            scenario.KnowledgeBaseId,
            scenario.TargetVersionId);
        Task second = ActivateInNewScopeAsync(
            scenario.TenantId,
            scenario.KnowledgeBaseId,
            competingVersionId);
        await Task.WhenAll(first, second);

        await using AsyncServiceScope inspectionScope = database.Services.CreateAsyncScope();
        RagDbContext inspection = inspectionScope.ServiceProvider
            .GetRequiredService<RagDbContext>();
        KnowledgeBaseVersion[] versions = await inspection.KnowledgeBaseVersions
            .AsNoTracking()
            .Where(version => version.KnowledgeBaseId == scenario.KnowledgeBaseId)
            .ToArrayAsync();
        Assert.Single(
            versions,
            version => version.Status == KnowledgeBaseVersionStatus.Active);
        Assert.DoesNotContain(
            versions,
            version => version.Status == KnowledgeBaseVersionStatus.Ready);
    }

    [Fact]
    public async Task Active_version_chunks_reject_insert_update_and_delete()
    {
        VersionScenario scenario = await SeedScenarioAsync();
        await ActivateInNewScopeAsync(
            scenario.TenantId,
            scenario.KnowledgeBaseId,
            scenario.TargetVersionId);

        Exception update = await CaptureDatabaseFailureAsync(
            context => context.DocumentChunks
                .Where(chunk => chunk.Id == scenario.TargetChunkId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(chunk => chunk.Content, "changed")));
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, FindPostgres(update).SqlState);

        Exception delete = await CaptureDatabaseFailureAsync(
            context => context.DocumentChunks
                .Where(chunk => chunk.Id == scenario.TargetChunkId)
                .ExecuteDeleteAsync());
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, FindPostgres(delete).SqlState);

        Exception insert = await CaptureDatabaseFailureAsync(async context =>
        {
            context.DocumentChunks.Add(CreateChunk(
                scenario.TenantId,
                scenario.KnowledgeBaseId,
                scenario.TargetVersionId,
                scenario.TargetDocumentId,
                chunkIndex: 1));
            await context.SaveChangesAsync();
        });
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, FindPostgres(insert).SqlState);
    }

    [Fact]
    public async Task Maintenance_archives_only_stale_ready_versions_superseded_by_active()
    {
        VersionScenario scenario = await SeedScenarioAsync();
        Guid freshVersionId;
        await using (AsyncServiceScope seedScope = database.Services.CreateAsyncScope())
        {
            RagDbContext context = seedScope.ServiceProvider.GetRequiredService<RagDbContext>();
            VersionSeed fresh = await AddReadyVersionAsync(
                context,
                scenario.TenantId,
                scenario.KnowledgeBaseId,
                hasChunk: true);
            freshVersionId = fresh.VersionId;
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE knowledge_base_versions
                SET updated_at = {DateTimeOffset.UtcNow.AddHours(-48)}
                WHERE id = {scenario.TargetVersionId}
                """);
        }

        await using AsyncServiceScope maintenanceScope = database.Services.CreateAsyncScope();
        IKnowledgeBaseVersionMaintenance maintenance = maintenanceScope.ServiceProvider
            .GetRequiredService<IKnowledgeBaseVersionMaintenance>();
        int archived = await maintenance.ArchiveSupersededReadyVersionsAsync(
            CancellationToken.None);

        Assert.Equal(1, archived);
        RagDbContext inspection = maintenanceScope.ServiceProvider
            .GetRequiredService<RagDbContext>();
        Dictionary<Guid, KnowledgeBaseVersionStatus> statuses = await inspection
            .KnowledgeBaseVersions
            .AsNoTracking()
            .Where(version => version.KnowledgeBaseId == scenario.KnowledgeBaseId)
            .ToDictionaryAsync(version => version.Id, version => version.Status);
        Assert.Equal(
            KnowledgeBaseVersionStatus.Archived,
            statuses[scenario.TargetVersionId]);
        Assert.Equal(KnowledgeBaseVersionStatus.Ready, statuses[freshVersionId]);
        Assert.Equal(
            KnowledgeBaseVersionStatus.Active,
            statuses[scenario.PreviousVersionId]);
    }

    private async Task ActivateInNewScopeAsync(
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId)
    {
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        IKnowledgeBaseVersionActivator activator = scope.ServiceProvider
            .GetRequiredService<IKnowledgeBaseVersionActivator>();
        await activator.ActivateAsync(
            tenantId,
            knowledgeBaseId,
            versionId,
            CancellationToken.None);
    }

    private async Task<Exception> CaptureDatabaseFailureAsync(
        Func<RagDbContext, Task> operation)
    {
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        return await Assert.ThrowsAnyAsync<Exception>(() => operation(context));
    }

    private async Task<VersionScenario> SeedScenarioAsync(bool targetHasChunk = true)
    {
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        Guid tenantId = Guid.NewGuid();
        Guid knowledgeBaseId = Guid.NewGuid();
        context.AddRange(
            Tenant.Create(tenantId, $"activation-tenant-{tenantId:N}"),
            KnowledgeBase.Create(
                knowledgeBaseId,
                tenantId,
                $"activation-kb-{knowledgeBaseId:N}"));
        await context.SaveChangesAsync();

        VersionSeed previous = await AddReadyVersionAsync(
            context,
            tenantId,
            knowledgeBaseId,
            hasChunk: true);
        previous.Version.Activate();
        await context.SaveChangesAsync();
        VersionSeed target = await AddReadyVersionAsync(
            context,
            tenantId,
            knowledgeBaseId,
            targetHasChunk);
        return new VersionScenario(
            tenantId,
            knowledgeBaseId,
            previous.VersionId,
            target.VersionId,
            target.DocumentId,
            target.ChunkId);
    }

    private static async Task<VersionSeed> AddReadyVersionAsync(
        RagDbContext context,
        Guid tenantId,
        Guid knowledgeBaseId,
        bool hasChunk)
    {
        Guid versionId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        var version = KnowledgeBaseVersion.Create(
            versionId,
            tenantId,
            knowledgeBaseId,
            "integration-test-model",
            RagDatabaseConstants.EmbeddingDimensions);
        version.MarkProcessing();
        version.MarkReady();
        var document = Document.Create(
            documentId,
            tenantId,
            knowledgeBaseId,
            versionId,
            $"{documentId:N}.txt",
            $"tests/{documentId:N}.txt",
            "text/plain",
            new string('a', 64),
            4);
        document.MarkProcessing();
        document.MarkIndexed();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var job = IngestionJob.Create(
            Guid.NewGuid(),
            tenantId,
            knowledgeBaseId,
            versionId,
            documentId,
            now,
            maxAttempts: 1);
        Guid lockToken = Guid.NewGuid();
        job.Acquire(lockToken, now, TimeSpan.FromMinutes(1));
        job.Complete(lockToken);
        context.AddRange(version, document, job);
        DocumentChunk? chunk = hasChunk
            ? CreateChunk(tenantId, knowledgeBaseId, versionId, documentId, chunkIndex: 0)
            : null;
        if (chunk is not null)
        {
            context.DocumentChunks.Add(chunk);
        }

        await context.SaveChangesAsync();
        return new VersionSeed(version, documentId, chunk?.Id ?? Guid.Empty);
    }

    private static DocumentChunk CreateChunk(
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        Guid documentId,
        int chunkIndex) =>
        DocumentChunk.Create(
            Guid.NewGuid(),
            tenantId,
            knowledgeBaseId,
            versionId,
            documentId,
            chunkIndex,
            "test",
            new string('b', 64),
            tokenCount: 1,
            startOffset: 0,
            endOffset: 4,
            new string('c', 64),
            new float[RagDatabaseConstants.EmbeddingDimensions]);

    private static PostgresException FindPostgres(Exception exception)
    {
        Exception? current = exception;
        while (current is not null)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }

            current = current.InnerException;
        }

        throw new Xunit.Sdk.XunitException(
            $"Expected a PostgreSQL failure, but received {exception.GetType().Name}.");
    }

    private sealed record VersionSeed(
        KnowledgeBaseVersion Version,
        Guid DocumentId,
        Guid ChunkId)
    {
        public Guid VersionId => Version.Id;
    }

    private sealed record VersionScenario(
        Guid TenantId,
        Guid KnowledgeBaseId,
        Guid PreviousVersionId,
        Guid TargetVersionId,
        Guid TargetDocumentId,
        Guid TargetChunkId);
}
