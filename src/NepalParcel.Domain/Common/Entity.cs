namespace NepalParcel.Domain.Common;

/// <summary>
/// Base type for all entities. Identifiers are generated in the domain (UUID v7, time-ordered)
/// so aggregates are valid before they are persisted.
/// </summary>
public abstract class Entity
{
    protected Entity()
        : this(Guid.CreateVersion7())
    {
    }

    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("Entity id cannot be empty.");
        }

        Id = id;
    }

    public Guid Id { get; private init; }

    public override bool Equals(object? obj) =>
        obj is Entity other && other.GetType() == GetType() && other.Id == Id;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
