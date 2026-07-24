using Rag.Domain.Common;

namespace Rag.Domain.Entities;

public sealed class QueryLog : CreatedEntity
{
    private QueryLog(
        Guid id,
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        Guid? chatbotId,
        string queryHash,
        int resultCount,
        bool degraded,
        int durationMilliseconds)
        : base(id)
    {
        TenantId = DomainGuard.Required(tenantId, nameof(tenantId));
        KnowledgeBaseId = DomainGuard.Required(knowledgeBaseId, nameof(knowledgeBaseId));
        VersionId = DomainGuard.Required(versionId, nameof(versionId));
        QueryHash = DomainGuard.Sha256(queryHash, nameof(queryHash));

        if (resultCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resultCount));
        }

        if (durationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        }

        ChatbotId = chatbotId;
        ResultCount = resultCount;
        Degraded = degraded;
        DurationMilliseconds = durationMilliseconds;
    }

    private QueryLog()
    {
        QueryHash = string.Empty;
    }

    public Guid TenantId { get; private set; }

    public Guid KnowledgeBaseId { get; private set; }

    public Guid VersionId { get; private set; }

    public Guid? ChatbotId { get; private set; }

    public string QueryHash { get; private set; }

    public int ResultCount { get; private set; }

    public bool Degraded { get; private set; }

    public int DurationMilliseconds { get; private set; }

    public static QueryLog Create(
        Guid id,
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        Guid? chatbotId,
        string queryHash,
        int resultCount,
        bool degraded,
        int durationMilliseconds) =>
        new(
            id,
            tenantId,
            knowledgeBaseId,
            versionId,
            chatbotId,
            queryHash,
            resultCount,
            degraded,
            durationMilliseconds);
}

