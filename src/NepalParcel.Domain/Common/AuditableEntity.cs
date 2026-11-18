namespace NepalParcel.Domain.Common;

/// <summary>
/// Marks an entity whose audit columns are maintained automatically by the persistence layer.
/// </summary>
public interface IAuditableEntity
{
    DateTimeOffset CreatedAtUtc { get; }

    string? CreatedBy { get; }

    DateTimeOffset? UpdatedAtUtc { get; }

    string? UpdatedBy { get; }

    void MarkCreated(DateTimeOffset atUtc, string? by);

    void MarkUpdated(DateTimeOffset atUtc, string? by);
}

public abstract class AuditableEntity : Entity, IAuditableEntity
{
    protected AuditableEntity()
    {
    }

    protected AuditableEntity(Guid id)
        : base(id)
    {
    }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public string? CreatedBy { get; private set; }

    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    public string? UpdatedBy { get; private set; }

    public void MarkCreated(DateTimeOffset atUtc, string? by)
    {
        CreatedAtUtc = atUtc;
        CreatedBy = by;
    }

    public void MarkUpdated(DateTimeOffset atUtc, string? by)
    {
        UpdatedAtUtc = atUtc;
        UpdatedBy = by;
    }
}
