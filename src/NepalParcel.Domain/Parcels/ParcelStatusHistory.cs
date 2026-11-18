using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Parcels;

/// <summary>
/// One immutable step in a parcel's journey. History is append-only: rows are never updated or deleted,
/// and a parcel can never be hard-deleted while history exists (foreign keys are restrictive).
/// </summary>
public sealed class ParcelStatusHistory : Entity
{
    private ParcelStatusHistory()
    {
    }

    internal ParcelStatusHistory(
        Guid parcelId,
        ParcelStatusCode? from,
        ParcelStatusCode to,
        DateTimeOffset occurredAtUtc,
        Guid? branchId,
        Guid? warehouseId,
        Guid? changedByUserId,
        string? changedByName,
        string? remarks)
    {
        ParcelId = parcelId;
        FromStatusId = from is null ? null : (int)from.Value;
        ToStatusId = (int)to;
        OccurredAtUtc = occurredAtUtc;
        BranchId = branchId;
        WarehouseId = warehouseId;
        ChangedByUserId = changedByUserId;
        ChangedByName = Guard.Optional(changedByName, "Changed by", 150);
        Remarks = Guard.Optional(remarks, "Remarks", 500);
    }

    public Guid ParcelId { get; private init; }

    public int? FromStatusId { get; private init; }

    public int ToStatusId { get; private init; }

    public DateTimeOffset OccurredAtUtc { get; private init; }

    public Guid? BranchId { get; private init; }

    public Guid? WarehouseId { get; private init; }

    public Guid? ChangedByUserId { get; private init; }

    /// <summary>Snapshot of the staff member's name at the time, so history stays readable if the account changes.</summary>
    public string? ChangedByName { get; private init; }

    public string? Remarks { get; private init; }
}
