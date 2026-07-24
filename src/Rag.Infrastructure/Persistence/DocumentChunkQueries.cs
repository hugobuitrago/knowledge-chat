using Rag.Domain.Entities;

namespace Rag.Infrastructure.Persistence;

public static class DocumentChunkQueries
{
    public static IQueryable<DocumentChunk> ForKnowledgeBaseVersion(
        this IQueryable<DocumentChunk> source,
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Where(chunk =>
            chunk.TenantId == tenantId &&
            chunk.KnowledgeBaseId == knowledgeBaseId &&
            chunk.VersionId == versionId);
    }
}

