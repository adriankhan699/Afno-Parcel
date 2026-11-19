using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Branches;

/// <summary>
/// Where a staff member works: a branch, and optionally one warehouse of that branch. A person has at most one active
/// assignment. Changing it ends the old one and starts a new one, so the history of who worked where is kept.
/// </summary>
public sealed class StaffAssignment : Entity
{
    private StaffAssignment()
    {
    }

    public Guid UserId { get; private init; }

    public Guid BranchId { get; private init; }

    /// <summary>When set, the person works at this warehouse only; otherwise at the branch and all of its warehouses.</summary>
    public Guid? WarehouseId { get; private init; }

    public DateTimeOffset AssignedAtUtc { get; private init; }

    public Guid? AssignedByUserId { get; private init; }

    public DateTimeOffset? EndedAtUtc { get; private set; }

    public Guid? EndedByUserId { get; private set; }

    public bool IsActive => EndedAtUtc is null;

    public static StaffAssignment Create(Guid userId, Guid branchId, Guid? warehouseId, DateTimeOffset assignedAtUtc, Guid? assignedByUserId) =>
        new()
        {
            UserId = Guard.NotEmpty(userId, "Staff member"),
            BranchId = Guard.NotEmpty(branchId, "Branch"),
            WarehouseId = warehouseId,
            AssignedAtUtc = assignedAtUtc,
            AssignedByUserId = assignedByUserId,
        };

    public void End(DateTimeOffset endedAtUtc, Guid? endedByUserId)
    {
        if (EndedAtUtc is not null)
        {
            throw new DomainException("This assignment has already ended.");
        }

        EndedAtUtc = endedAtUtc;
        EndedByUserId = endedByUserId;
    }

    /// <summary>Whether this assignment lets the person work at the warehouse (same branch, and the same warehouse if one is named).</summary>
    public bool CoversWarehouse(Guid warehouseBranchId, Guid warehouseId) =>
        IsActive && BranchId == warehouseBranchId && (WarehouseId is null || WarehouseId == warehouseId);
}
