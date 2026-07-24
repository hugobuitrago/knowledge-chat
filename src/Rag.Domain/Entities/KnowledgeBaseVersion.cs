using Rag.Domain.Common;
using Rag.Domain.Enums;

namespace Rag.Domain.Entities;

public sealed class KnowledgeBaseVersion : AuditableEntity
{
    private KnowledgeBaseVersion(
        Guid id,
        Guid tenantId,
        Guid knowledgeBaseId,
        string embeddingModel,
        int embeddingDimensions)
        : base(id)
    {
        TenantId = DomainGuard.Required(tenantId, nameof(tenantId));
        KnowledgeBaseId = DomainGuard.Required(knowledgeBaseId, nameof(knowledgeBaseId));
        EmbeddingModel = DomainGuard.Required(embeddingModel, 200, nameof(embeddingModel));

        if (embeddingDimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(embeddingDimensions),
                "Embedding dimensions must be positive.");
        }

        EmbeddingDimensions = embeddingDimensions;
        Status = KnowledgeBaseVersionStatus.Pending;
    }

    private KnowledgeBaseVersion()
    {
        EmbeddingModel = string.Empty;
    }

    public Guid TenantId { get; private set; }

    public Guid KnowledgeBaseId { get; private set; }

    public KnowledgeBaseVersionStatus Status { get; private set; }

    public string EmbeddingModel { get; private set; }

    public int EmbeddingDimensions { get; private set; }

    public static KnowledgeBaseVersion Create(
        Guid id,
        Guid tenantId,
        Guid knowledgeBaseId,
        string embeddingModel,
        int embeddingDimensions) =>
        new(id, tenantId, knowledgeBaseId, embeddingModel, embeddingDimensions);

    public void MarkProcessing()
    {
        if (Status == KnowledgeBaseVersionStatus.Processing)
        {
            return;
        }

        EnsureStatus(KnowledgeBaseVersionStatus.Pending);
        Status = KnowledgeBaseVersionStatus.Processing;
    }

    public void MarkReady()
    {
        EnsureStatus(KnowledgeBaseVersionStatus.Processing);
        Status = KnowledgeBaseVersionStatus.Ready;
    }

    public void Activate()
    {
        EnsureStatus(KnowledgeBaseVersionStatus.Ready);
        Status = KnowledgeBaseVersionStatus.Active;
    }

    public void Archive()
    {
        if (Status is not (KnowledgeBaseVersionStatus.Active or KnowledgeBaseVersionStatus.Ready))
        {
            throw new InvalidOperationException($"Cannot archive a version in {Status} state.");
        }

        Status = KnowledgeBaseVersionStatus.Archived;
    }

    public void MarkFailed()
    {
        if (Status is KnowledgeBaseVersionStatus.Active or KnowledgeBaseVersionStatus.Archived)
        {
            throw new InvalidOperationException($"Cannot fail a version in {Status} state.");
        }

        Status = KnowledgeBaseVersionStatus.Failed;
    }

    public void PrepareForRetry()
    {
        EnsureStatus(KnowledgeBaseVersionStatus.Failed);
        Status = KnowledgeBaseVersionStatus.Pending;
    }

    private void EnsureStatus(KnowledgeBaseVersionStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException(
                $"Expected version state {expected}, but current state is {Status}.");
        }
    }
}

