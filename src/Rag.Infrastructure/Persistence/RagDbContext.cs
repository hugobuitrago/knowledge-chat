using Microsoft.EntityFrameworkCore;
using Rag.Domain.Entities;

namespace Rag.Infrastructure.Persistence;

public sealed class RagDbContext(DbContextOptions<RagDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Chatbot> Chatbots => Set<Chatbot>();

    public DbSet<KnowledgeBase> KnowledgeBases => Set<KnowledgeBase>();

    public DbSet<KnowledgeBaseVersion> KnowledgeBaseVersions => Set<KnowledgeBaseVersion>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();

    public DbSet<IngestionJob> IngestionJobs => Set<IngestionJob>();

    public DbSet<QueryLog> QueryLogs => Set<QueryLog>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RagDbContext).Assembly);
    }
}

