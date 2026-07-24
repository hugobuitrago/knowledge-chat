using Rag.Domain.Common;

namespace Rag.Domain.Entities;

public sealed class KnowledgeBase : AuditableEntity
{
    private KnowledgeBase(Guid id, Guid tenantId, string name)
        : base(id)
    {
        TenantId = DomainGuard.Required(tenantId, nameof(tenantId));
        Name = DomainGuard.Required(name, 200, nameof(name));
    }

    private KnowledgeBase()
    {
        Name = string.Empty;
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public static KnowledgeBase Create(Guid id, Guid tenantId, string name) =>
        new(id, tenantId, name);

    public static KnowledgeBase Create(Guid tenantId, string name) =>
        new(Guid.NewGuid(), tenantId, name);
}

