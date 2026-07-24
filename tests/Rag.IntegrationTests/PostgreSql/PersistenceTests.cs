using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rag.Domain.Entities;
using Rag.Infrastructure.Persistence;

namespace Rag.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PersistenceTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task Migrations_are_idempotent_and_create_the_complete_schema()
    {
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();

        await context.Database.MigrateAsync(CancellationToken.None);
        await context.Database.MigrateAsync(CancellationToken.None);

        string[] appliedMigrations = (await context.Database
                .GetAppliedMigrationsAsync(CancellationToken.None))
            .ToArray();
        NpgsqlDataSource dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using NpgsqlCommand tableCommand = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_name IN (
                'tenants', 'chatbots', 'knowledge_bases', 'knowledge_base_versions',
                'documents', 'document_chunks', 'ingestion_jobs', 'query_logs',
                'idempotency_records')
            """);
        long tableCount = (long)(await tableCommand.ExecuteScalarAsync(CancellationToken.None))!;
        await using NpgsqlCommand extensionCommand = dataSource.CreateCommand(
            "SELECT extversion FROM pg_extension WHERE extname = 'vector'");
        string? vectorVersion = (string?)await extensionCommand.ExecuteScalarAsync(CancellationToken.None);

        Assert.Equal(2, appliedMigrations.Length);
        Assert.Equal(9, tableCount);
        Assert.False(string.IsNullOrWhiteSpace(vectorVersion));
    }

    [Fact]
    public async Task Scoped_chunk_query_isolates_tenants_and_round_trips_vector_and_search_vector()
    {
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        PersistedScope tenantA = AddScope(context, "tenant-a", 1F);
        _ = AddScope(context, "tenant-b", 2F);
        await context.SaveChangesAsync(CancellationToken.None);
        context.ChangeTracker.Clear();

        DocumentChunk[] chunks = await context.DocumentChunks
            .AsNoTracking()
            .ForKnowledgeBaseVersion(
                tenantA.TenantId,
                tenantA.KnowledgeBaseId,
                tenantA.VersionId)
            .ToArrayAsync(CancellationToken.None);

        Assert.Single(chunks);
        Assert.Equal(tenantA.ChunkId, chunks[0].Id);
        Assert.Equal(RagDatabaseConstants.EmbeddingDimensions, chunks[0].Embedding.Length);
        Assert.Equal(1F, chunks[0].Embedding[0]);

        NpgsqlDataSource dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using NpgsqlCommand command = dataSource.CreateCommand(
            """
            SELECT search_vector @@ plainto_tsquery('simple', @term)
            FROM document_chunks
            WHERE id = @chunk_id
            """);
        command.Parameters.AddWithValue("term", "alpha");
        command.Parameters.AddWithValue("chunk_id", tenantA.ChunkId);
        bool lexicalMatch = (bool)(await command.ExecuteScalarAsync(CancellationToken.None))!;

        Assert.True(lexicalMatch);
    }

    [Fact]
    public async Task Composite_foreign_keys_reject_cross_tenant_documents()
    {
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var tenantA = Tenant.Create($"tenant-a-{Guid.NewGuid():N}");
        var tenantB = Tenant.Create($"tenant-b-{Guid.NewGuid():N}");
        var knowledgeBase = KnowledgeBase.Create(tenantA.Id, $"kb-{Guid.NewGuid():N}");
        KnowledgeBaseVersion version = CreateVersion(tenantA.Id, knowledgeBase.Id);
        context.AddRange(tenantA, tenantB, knowledgeBase, version);
        await context.SaveChangesAsync(CancellationToken.None);

        var invalidDocument = Document.Create(
            Guid.NewGuid(),
            tenantB.Id,
            knowledgeBase.Id,
            version.Id,
            "cross-tenant.txt",
            "cross-tenant/object",
            "text/plain",
            Hash("cross-tenant"),
            1);
        context.Documents.Add(invalidDocument);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            context.SaveChangesAsync(CancellationToken.None));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
    }

    [Fact]
    public async Task Partial_unique_index_allows_only_one_active_version_per_knowledge_base()
    {
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var tenant = Tenant.Create($"tenant-{Guid.NewGuid():N}");
        var knowledgeBase = KnowledgeBase.Create(tenant.Id, $"kb-{Guid.NewGuid():N}");
        context.AddRange(tenant, knowledgeBase);
        await context.SaveChangesAsync(CancellationToken.None);

        KnowledgeBaseVersion first = CreateActiveVersion(tenant.Id, knowledgeBase.Id);
        KnowledgeBaseVersion second = CreateActiveVersion(tenant.Id, knowledgeBase.Id);
        context.KnowledgeBaseVersions.AddRange(first, second);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            context.SaveChangesAsync(CancellationToken.None));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
    }

    [Fact]
    public async Task Xmin_detects_concurrent_updates_and_timestamps_are_utc()
    {
        Guid tenantId = Guid.NewGuid();
        await using (AsyncServiceScope seedScope = database.Services.CreateAsyncScope())
        {
            RagDbContext seedContext = seedScope.ServiceProvider.GetRequiredService<RagDbContext>();
            Tenant tenant = Tenant.Create(tenantId, $"tenant-{tenantId:N}");
            seedContext.Tenants.Add(tenant);
            await seedContext.SaveChangesAsync(CancellationToken.None);

            Assert.Equal(TimeSpan.Zero, tenant.CreatedAt.Offset);
            Assert.Equal(TimeSpan.Zero, tenant.UpdatedAt.Offset);
            Assert.NotEqual(default, tenant.CreatedAt);
        }

        await using AsyncServiceScope firstScope = database.Services.CreateAsyncScope();
        await using AsyncServiceScope secondScope = database.Services.CreateAsyncScope();
        RagDbContext firstContext = firstScope.ServiceProvider.GetRequiredService<RagDbContext>();
        RagDbContext secondContext = secondScope.ServiceProvider.GetRequiredService<RagDbContext>();
        Tenant first = await firstContext.Tenants.SingleAsync(
            tenant => tenant.Id == tenantId,
            CancellationToken.None);
        Tenant second = await secondContext.Tenants.SingleAsync(
            tenant => tenant.Id == tenantId,
            CancellationToken.None);

        first.Rename($"first-{Guid.NewGuid():N}");
        await firstContext.SaveChangesAsync(CancellationToken.None);
        second.Rename($"second-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            secondContext.SaveChangesAsync(CancellationToken.None));
    }

    private static PersistedScope AddScope(RagDbContext context, string prefix, float marker)
    {
        Guid tenantId = Guid.NewGuid();
        Guid knowledgeBaseId = Guid.NewGuid();
        Guid versionId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        Guid chunkId = Guid.NewGuid();
        var tenant = Tenant.Create(tenantId, $"{prefix}-{tenantId:N}");
        KnowledgeBase knowledgeBase = KnowledgeBase.Create(
            knowledgeBaseId,
            tenantId,
            $"kb-{knowledgeBaseId:N}");
        KnowledgeBaseVersion version = CreateVersion(tenantId, knowledgeBaseId, versionId);
        var document = Document.Create(
            documentId,
            tenantId,
            knowledgeBaseId,
            versionId,
            $"{prefix}.txt",
            $"{prefix}/{documentId:N}",
            "text/plain",
            Hash($"document-{documentId:N}"),
            100);
        var embedding = new float[RagDatabaseConstants.EmbeddingDimensions];
        embedding[0] = marker;
        DocumentChunk chunk = DocumentChunk.Create(
            chunkId,
            tenantId,
            knowledgeBaseId,
            versionId,
            documentId,
            0,
            $"alpha content for {prefix}",
            Hash($"chunk-{chunkId:N}"),
            5,
            0,
            26,
            Hash("integration-test-chunking"),
            embedding,
            "{\"source\":\"integration-test\"}");
        context.AddRange(tenant, knowledgeBase, version, document, chunk);

        return new PersistedScope(tenantId, knowledgeBaseId, versionId, chunkId);
    }

    private static KnowledgeBaseVersion CreateVersion(
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid? versionId = null) =>
        KnowledgeBaseVersion.Create(
            versionId ?? Guid.NewGuid(),
            tenantId,
            knowledgeBaseId,
            "test-embedding-model",
            RagDatabaseConstants.EmbeddingDimensions);

    private static KnowledgeBaseVersion CreateActiveVersion(Guid tenantId, Guid knowledgeBaseId)
    {
        KnowledgeBaseVersion version = CreateVersion(tenantId, knowledgeBaseId);
        version.MarkProcessing();
        version.MarkReady();
        version.Activate();
        return version;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record PersistedScope(
        Guid TenantId,
        Guid KnowledgeBaseId,
        Guid VersionId,
        Guid ChunkId);
}

