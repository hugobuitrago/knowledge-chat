using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NpgsqlTypes;
using Pgvector;
using Rag.Domain.Common;
using Rag.Domain.Entities;
using Rag.Domain.Enums;

namespace Rag.Infrastructure.Persistence.Configurations;

internal static class ConfigurationHelpers
{
    public static void ConfigureCreatedEntity<TEntity>(
        EntityTypeBuilder<TEntity> builder,
        string tableName)
        where TEntity : CreatedEntity
    {
        builder.ToTable(tableName);
        builder.HasKey(entity => entity.Id).HasName($"pk_{tableName}");
        builder.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(entity => entity.CreatedAt).HasColumnName("created_at").IsRequired();
    }

    public static void ConfigureAuditableEntity<TEntity>(
        EntityTypeBuilder<TEntity> builder,
        string tableName)
        where TEntity : AuditableEntity
    {
        ConfigureCreatedEntity(builder, tableName);
        builder.Property(entity => entity.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property<uint>("xmin").IsRowVersion();
    }
}

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        ConfigurationHelpers.ConfigureAuditableEntity(builder, "tenants");
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.IsActive).HasColumnName("is_active").IsRequired();
    }
}

internal sealed class KnowledgeBaseConfiguration : IEntityTypeConfiguration<KnowledgeBase>
{
    public void Configure(EntityTypeBuilder<KnowledgeBase> builder)
    {
        ConfigurationHelpers.ConfigureAuditableEntity(builder, "knowledge_bases");
        builder.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.HasAlternateKey(entity => new { entity.TenantId, entity.Id })
            .HasName("ak_knowledge_bases_tenant_id_id");
        builder.HasIndex(entity => new { entity.TenantId, entity.Name })
            .IsUnique()
            .HasDatabaseName("ux_knowledge_bases_tenant_name");
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(entity => entity.TenantId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_knowledge_bases_tenants");
    }
}

internal sealed class ChatbotConfiguration : IEntityTypeConfiguration<Chatbot>
{
    public void Configure(EntityTypeBuilder<Chatbot> builder)
    {
        ConfigurationHelpers.ConfigureAuditableEntity(builder, "chatbots");
        builder.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(entity => entity.KnowledgeBaseId).HasColumnName("knowledge_base_id").IsRequired();
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.HasAlternateKey(entity => new { entity.TenantId, entity.Id })
            .HasName("ak_chatbots_tenant_id_id");
        builder.HasIndex(entity => new { entity.TenantId, entity.Name })
            .IsUnique()
            .HasDatabaseName("ux_chatbots_tenant_name");
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(entity => entity.TenantId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_chatbots_tenants");
        builder.HasOne<KnowledgeBase>()
            .WithMany()
            .HasForeignKey(entity => new { entity.TenantId, entity.KnowledgeBaseId })
            .HasPrincipalKey(entity => new { entity.TenantId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_chatbots_knowledge_bases");
    }
}

internal sealed class KnowledgeBaseVersionConfiguration :
    IEntityTypeConfiguration<KnowledgeBaseVersion>
{
    public void Configure(EntityTypeBuilder<KnowledgeBaseVersion> builder)
    {
        ConfigurationHelpers.ConfigureAuditableEntity(builder, "knowledge_base_versions");
        builder.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(entity => entity.KnowledgeBaseId).HasColumnName("knowledge_base_id").IsRequired();
        builder.Property(entity => entity.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(entity => entity.EmbeddingModel)
            .HasColumnName("embedding_model")
            .HasMaxLength(200)
            .IsRequired();
        builder.Property(entity => entity.EmbeddingDimensions)
            .HasColumnName("embedding_dimensions")
            .IsRequired();
        builder.HasAlternateKey(entity => new { entity.TenantId, entity.KnowledgeBaseId, entity.Id })
            .HasName("ak_kb_versions_scope_id");
        builder.HasIndex(entity => new { entity.TenantId, entity.KnowledgeBaseId })
            .HasDatabaseName("ix_kb_versions_scope");
        builder.HasIndex(entity => new { entity.TenantId, entity.KnowledgeBaseId, entity.Status })
            .IsUnique()
            .HasFilter("status = 'Active'")
            .HasDatabaseName("ux_kb_versions_one_active");
        builder.HasOne<KnowledgeBase>()
            .WithMany()
            .HasForeignKey(entity => new { entity.TenantId, entity.KnowledgeBaseId })
            .HasPrincipalKey(entity => new { entity.TenantId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_kb_versions_knowledge_bases");
    }
}

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        ConfigurationHelpers.ConfigureAuditableEntity(builder, "documents");
        builder.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(entity => entity.KnowledgeBaseId).HasColumnName("knowledge_base_id").IsRequired();
        builder.Property(entity => entity.VersionId).HasColumnName("version_id").IsRequired();
        builder.Property(entity => entity.FileName).HasColumnName("file_name").HasMaxLength(512).IsRequired();
        builder.Property(entity => entity.StorageObjectKey)
            .HasColumnName("storage_object_key")
            .HasMaxLength(1024)
            .IsRequired();
        builder.Property(entity => entity.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(100)
            .IsRequired();
        builder.Property(entity => entity.ContentHash)
            .HasColumnName("content_hash")
            .HasColumnType("character(64)")
            .IsRequired();
        builder.Property(entity => entity.SizeBytes).HasColumnName("size_bytes").IsRequired();
        builder.Property(entity => entity.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(entity => entity.ErrorMessage)
            .HasColumnName("error_message")
            .HasMaxLength(2000);
        builder.HasAlternateKey(entity =>
                new { entity.TenantId, entity.KnowledgeBaseId, entity.VersionId, entity.Id })
            .HasName("ak_documents_scope_id");
        builder.HasIndex(entity => new { entity.TenantId, entity.KnowledgeBaseId, entity.VersionId })
            .HasDatabaseName("ix_documents_scope");
        builder.HasIndex(entity => new { entity.VersionId, entity.ContentHash })
            .IsUnique()
            .HasDatabaseName("ux_documents_version_hash");
        builder.HasOne<KnowledgeBaseVersion>()
            .WithMany()
            .HasForeignKey(entity => new { entity.TenantId, entity.KnowledgeBaseId, entity.VersionId })
            .HasPrincipalKey(entity => new { entity.TenantId, entity.KnowledgeBaseId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_documents_kb_versions");
    }
}

internal sealed class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    private static readonly ValueConverter<float[], Vector> EmbeddingConverter = new(
        embedding => new Vector(embedding),
        vector => vector.ToArray());

    private static readonly ValueComparer<float[]> EmbeddingComparer = new(
        (left, right) => left != null && right != null && left.SequenceEqual(right),
        embedding => GetEmbeddingHashCode(embedding),
        embedding => embedding.ToArray());

    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        ConfigurationHelpers.ConfigureCreatedEntity(builder, "document_chunks");
        builder.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(entity => entity.KnowledgeBaseId).HasColumnName("knowledge_base_id").IsRequired();
        builder.Property(entity => entity.VersionId).HasColumnName("version_id").IsRequired();
        builder.Property(entity => entity.DocumentId).HasColumnName("document_id").IsRequired();
        builder.Property(entity => entity.ChunkIndex).HasColumnName("chunk_index").IsRequired();
        builder.Property(entity => entity.Content).HasColumnName("content").HasColumnType("text").IsRequired();
        builder.Property(entity => entity.ContentHash)
            .HasColumnName("content_hash")
            .HasColumnType("character(64)")
            .IsRequired();
        builder.Property(entity => entity.TokenCount).HasColumnName("token_count").IsRequired();
        builder.Property(entity => entity.Embedding)
            .HasColumnName("embedding")
            .HasColumnType($"vector({RagDatabaseConstants.EmbeddingDimensions})")
            .HasConversion(EmbeddingConverter)
            .Metadata.SetValueComparer(EmbeddingComparer);
        builder.Property(entity => entity.MetadataJson)
            .HasColumnName("metadata")
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();
        builder.Property<NpgsqlTsVector>("SearchVector")
            .HasColumnName("search_vector")
            .HasColumnType("tsvector")
            .IsRequired()
            .HasComputedColumnSql(
                "to_tsvector('simple'::regconfig, coalesce(content, ''::text))",
                stored: true);
        builder.HasIndex(entity => new { entity.TenantId, entity.KnowledgeBaseId, entity.VersionId })
            .HasDatabaseName("ix_document_chunks_scope");
        builder.HasIndex(entity => new { entity.VersionId, entity.DocumentId, entity.ChunkIndex })
            .IsUnique()
            .HasDatabaseName("ux_document_chunks_position");
        builder.HasIndex(entity => new { entity.VersionId, entity.DocumentId, entity.ContentHash })
            .IsUnique()
            .HasDatabaseName("ux_document_chunks_hash");
        builder.HasIndex("SearchVector")
            .HasMethod("GIN")
            .HasDatabaseName("ix_document_chunks_search_vector");
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(entity =>
                new { entity.TenantId, entity.KnowledgeBaseId, entity.VersionId, entity.DocumentId })
            .HasPrincipalKey(entity =>
                new { entity.TenantId, entity.KnowledgeBaseId, entity.VersionId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_document_chunks_documents");
    }

    private static int GetEmbeddingHashCode(float[] embedding)
    {
        var hash = new HashCode();
        foreach (float value in embedding)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

internal sealed class IngestionJobConfiguration : IEntityTypeConfiguration<IngestionJob>
{
    public void Configure(EntityTypeBuilder<IngestionJob> builder)
    {
        ConfigurationHelpers.ConfigureAuditableEntity(builder, "ingestion_jobs");
        builder.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(entity => entity.KnowledgeBaseId).HasColumnName("knowledge_base_id").IsRequired();
        builder.Property(entity => entity.VersionId).HasColumnName("version_id").IsRequired();
        builder.Property(entity => entity.DocumentId).HasColumnName("document_id").IsRequired();
        builder.Property(entity => entity.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(entity => entity.Attempts).HasColumnName("attempts").IsRequired();
        builder.Property(entity => entity.MaxAttempts).HasColumnName("max_attempts").IsRequired();
        builder.Property(entity => entity.AvailableAt).HasColumnName("available_at").IsRequired();
        builder.Property(entity => entity.LockedUntil).HasColumnName("locked_until");
        builder.Property(entity => entity.LockToken).HasColumnName("lock_token");
        builder.Property(entity => entity.LastError).HasColumnName("last_error").HasMaxLength(2000);
        builder.HasIndex(entity => new { entity.Status, entity.AvailableAt })
            .HasDatabaseName("ix_ingestion_jobs_dequeue");
        builder.HasIndex(entity => new { entity.TenantId, entity.KnowledgeBaseId, entity.VersionId })
            .HasDatabaseName("ix_ingestion_jobs_scope");
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(entity =>
                new { entity.TenantId, entity.KnowledgeBaseId, entity.VersionId, entity.DocumentId })
            .HasPrincipalKey(entity =>
                new { entity.TenantId, entity.KnowledgeBaseId, entity.VersionId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_ingestion_jobs_documents");
    }
}

internal sealed class QueryLogConfiguration : IEntityTypeConfiguration<QueryLog>
{
    public void Configure(EntityTypeBuilder<QueryLog> builder)
    {
        ConfigurationHelpers.ConfigureCreatedEntity(builder, "query_logs");
        builder.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(entity => entity.KnowledgeBaseId).HasColumnName("knowledge_base_id").IsRequired();
        builder.Property(entity => entity.VersionId).HasColumnName("version_id").IsRequired();
        builder.Property(entity => entity.ChatbotId).HasColumnName("chatbot_id");
        builder.Property(entity => entity.QueryHash)
            .HasColumnName("query_hash")
            .HasColumnType("character(64)")
            .IsRequired();
        builder.Property(entity => entity.ResultCount).HasColumnName("result_count").IsRequired();
        builder.Property(entity => entity.Degraded).HasColumnName("degraded").IsRequired();
        builder.Property(entity => entity.DurationMilliseconds)
            .HasColumnName("duration_ms")
            .IsRequired();
        builder.HasIndex(entity =>
                new { entity.TenantId, entity.KnowledgeBaseId, entity.VersionId, entity.CreatedAt })
            .HasDatabaseName("ix_query_logs_scope_created");
        builder.HasOne<KnowledgeBaseVersion>()
            .WithMany()
            .HasForeignKey(entity => new { entity.TenantId, entity.KnowledgeBaseId, entity.VersionId })
            .HasPrincipalKey(entity => new { entity.TenantId, entity.KnowledgeBaseId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_query_logs_kb_versions");
        builder.HasOne<Chatbot>()
            .WithMany()
            .HasForeignKey(entity => new { entity.TenantId, entity.ChatbotId })
            .HasPrincipalKey(entity => new { entity.TenantId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_query_logs_chatbots");
    }
}

internal sealed class IdempotencyRecordConfiguration :
    IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        ConfigurationHelpers.ConfigureCreatedEntity(builder, "idempotency_records");
        builder.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(entity => entity.Key).HasColumnName("key").HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.Operation)
            .HasColumnName("operation")
            .HasMaxLength(100)
            .IsRequired();
        builder.Property(entity => entity.RequestHash)
            .HasColumnName("request_hash")
            .HasColumnType("character(64)")
            .IsRequired();
        builder.Property(entity => entity.ResponseStatusCode).HasColumnName("response_status_code");
        builder.Property(entity => entity.ResponseBodyJson)
            .HasColumnName("response_body")
            .HasColumnType("jsonb");
        builder.Property(entity => entity.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.HasIndex(entity => new { entity.TenantId, entity.Operation, entity.Key })
            .IsUnique()
            .HasDatabaseName("ux_idempotency_records_key");
        builder.HasIndex(entity => entity.ExpiresAt)
            .HasDatabaseName("ix_idempotency_records_expires_at");
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(entity => entity.TenantId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_idempotency_records_tenants");
    }
}
