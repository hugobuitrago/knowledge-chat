using Rag.Domain.Common;

namespace Rag.Domain.Entities;

public sealed class Chatbot : AuditableEntity
{
    private Chatbot(Guid id, Guid tenantId, Guid knowledgeBaseId, string name)
        : base(id)
    {
        TenantId = DomainGuard.Required(tenantId, nameof(tenantId));
        KnowledgeBaseId = DomainGuard.Required(knowledgeBaseId, nameof(knowledgeBaseId));
        Name = DomainGuard.Required(name, 200, nameof(name));
    }

    private Chatbot()
    {
        Name = string.Empty;
    }

    public Guid TenantId { get; private set; }

    public Guid KnowledgeBaseId { get; private set; }

    public string Name { get; private set; }

    public static Chatbot Create(
        Guid id,
        Guid tenantId,
        Guid knowledgeBaseId,
        string name) => new(id, tenantId, knowledgeBaseId, name);
}

