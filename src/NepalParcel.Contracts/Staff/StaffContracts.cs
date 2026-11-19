namespace NepalParcel.Contracts.Staff;

/// <summary>
/// Where a staff member works. A staff member has at most one active assignment; changing it keeps the old one as history.
/// WarehouseId is set when the person works at one warehouse of the branch, and empty when they work at the branch in general.
/// </summary>
public sealed record StaffAssignmentDto(
    Guid AssignmentId,
    Guid BranchId,
    string BranchName,
    Guid? WarehouseId,
    string? WarehouseName,
    DateTimeOffset AssignedAtUtc);

/// <summary>A staff account and where it works.</summary>
public sealed record StaffMemberResponse(
    Guid UserId,
    string FullName,
    string Email,
    IReadOnlyList<string> Roles,
    bool IsActive,
    StaffAssignmentDto? Assignment);

/// <summary>Assigns a staff member to a branch, and optionally to one of its warehouses.</summary>
public sealed record AssignStaffRequest(Guid BranchId, Guid? WarehouseId = null);

/// <summary>Filters for the staff list (SuperAdmin and BranchManager).</summary>
public sealed record StaffQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    /// <summary>Part of the name or email.</summary>
    public string? Search { get; init; }

    /// <summary>One of BranchManager, BookingStaff, WarehouseStaff, DeliveryAgent, Accountant, SuperAdmin.</summary>
    public string? Role { get; init; }

    public Guid? BranchId { get; init; }

    public Guid? WarehouseId { get; init; }

    /// <summary>true = only staff with no active assignment.</summary>
    public bool? Unassigned { get; init; }
}

/// <summary>The signed-in person's own assignment, or null when they have none.</summary>
public sealed record MyAssignmentResponse(StaffAssignmentDto? Assignment);
