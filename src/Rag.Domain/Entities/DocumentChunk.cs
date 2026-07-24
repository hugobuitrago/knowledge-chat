using Rag.Domain.Common;

namespace Rag.Domain.Entities;

public sealed class DocumentChunk : CreatedEntity
{
    private DocumentChunk(
        Guid id,
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        Guid documentId,
        int chunkIndex,
        string content,
        string contentHash,
        int tokenCount,
        float[] embedding,
        string metadataJson)
        : base(id)
    {
        TenantId = DomainGuard.Required(tenantId, nameof(tenantId));
        KnowledgeBaseId = DomainGuard.Required(knowledgeBaseId, nameof(knowledgeBaseId));
        VersionId = DomainGuard.Required(versionId, nameof(versionId));
        DocumentId = DomainGuard.Required(documentId, nameof(documentId));

        if (chunkIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        }

        if (tokenCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenCount));
        }

        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Length == 0)
        {
            throw new ArgumentException("Embedding cannot be empty.", nameof(embedding));
        }

        ChunkIndex = chunkIndex;
        Content = DomainGuard.Required(content, int.MaxValue, nameof(content));
        ContentHash = DomainGuard.Sha256(contentHash, nameof(contentHash));
        TokenCount = tokenCount;
        Embedding = embedding.ToArray();
        MetadataJson = DomainGuard.Required(metadataJson, int.MaxValue, nameof(metadataJson));
    }

    private DocumentChunk()
    {
        Content = string.Empty;
        ContentHash = string.Empty;
        Embedding = [];
        MetadataJson = "{}";
    }

    public Guid TenantId { get; private set; }

    public Guid KnowledgeBaseId { get; private set; }

    public Guid VersionId { get; private set; }

    public Guid DocumentId { get; private set; }

    public int ChunkIndex { get; private set; }

    public string Content { get; private set; }

    public string ContentHash { get; private set; }

    public int TokenCount { get; private set; }

    public float[] Embedding { get; private set; }

    public string MetadataJson { get; private set; }

    public static DocumentChunk Create(
        Guid id,
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        Guid documentId,
        int chunkIndex,
        string content,
        string contentHash,
        int tokenCount,
        float[] embedding,
        string metadataJson = "{}") =>
        new(
            id,
            tenantId,
            knowledgeBaseId,
            versionId,
            documentId,
            chunkIndex,
            content,
            contentHash,
            tokenCount,
            embedding,
            metadataJson);
}

