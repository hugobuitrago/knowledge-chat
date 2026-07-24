namespace Rag.Domain.Common;

public abstract class Entity
{
    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entity ID cannot be empty.", nameof(id));
        }

        Id = id;
    }

    protected Entity()
    {
    }

    public Guid Id { get; private set; }
}

public interface IHasCreatedAt
{
    DateTimeOffset CreatedAt { get; }
}

public interface IHasUpdatedAt
{
    DateTimeOffset UpdatedAt { get; }
}

public abstract class CreatedEntity : Entity, IHasCreatedAt
{
    protected CreatedEntity(Guid id)
        : base(id)
    {
    }

    protected CreatedEntity()
    {
    }

    public DateTimeOffset CreatedAt { get; private set; }
}

public abstract class AuditableEntity : CreatedEntity, IHasUpdatedAt
{
    protected AuditableEntity(Guid id)
        : base(id)
    {
    }

    protected AuditableEntity()
    {
    }

    public DateTimeOffset UpdatedAt { get; private set; }
}

