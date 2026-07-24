using Rag.Domain.Common;

namespace Rag.Domain.Entities;

public sealed class Tenant : AuditableEntity
{
    private Tenant(Guid id, string name)
        : base(id)
    {
        Name = DomainGuard.Required(name, 200, nameof(name));
        IsActive = true;
    }

    private Tenant()
    {
        Name = string.Empty;
    }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public static Tenant Create(Guid id, string name) => new(id, name);

    public static Tenant Create(string name) => new(Guid.NewGuid(), name);

    public void Rename(string name) => Name = DomainGuard.Required(name, 200, nameof(name));

    public void Deactivate() => IsActive = false;
}

