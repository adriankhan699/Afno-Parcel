using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.Application.Network;

/// <summary>
/// Who may work where. A super administrator may work and manage anywhere. Everyone else only works at the branch (and, if
/// the assignment names one, the warehouse) they are assigned to; a branch manager only manages their own branch.
/// </summary>
internal sealed class WorkplaceAccess(ICurrentUser currentUser, IStaffAssignmentRepository assignments)
{
    public bool IsSuperAdmin => currentUser.IsInRole(Roles.SuperAdmin);

    public Task<StaffAssignment?> GetMyAssignmentAsync(CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId
            ? assignments.GetActiveByUserIdAsync(userId, cancellationToken)
            : Task.FromResult<StaffAssignment?>(null);

    /// <summary>
    /// The branch a money report or list is limited to. Super administrators and accountants (finance staff at head office) may
    /// look at one branch or at everything; a branch manager only ever sees their own branch.
    /// </summary>
    public async Task<Guid?> ScopeFinanceBranchAsync(Guid? requested, CancellationToken cancellationToken)
    {
        if (IsSuperAdmin || currentUser.IsInRole(Roles.Accountant))
        {
            return requested;
        }

        if (!currentUser.IsInRole(Roles.BranchManager))
        {
            throw new ForbiddenException("Only finance staff can see this.");
        }

        var mine = await GetMyAssignmentAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ForbiddenException("You are not assigned to a branch yet. Ask a super administrator to assign you.");
        if (requested is { } branch && branch != mine.BranchId)
        {
            throw new ForbiddenException("A branch manager can only see their own branch.");
        }

        return mine.BranchId;
    }

    /// <summary>Super administrators, and branch managers assigned to this branch.</summary>
    public async Task EnsureCanManageBranchAsync(Guid branchId, CancellationToken cancellationToken)
    {
        if (IsSuperAdmin)
        {
            return;
        }

        if (!currentUser.IsInRole(Roles.BranchManager))
        {
            throw new ForbiddenException("Only a super administrator or a branch manager can do this.");
        }

        var mine = await GetMyAssignmentAsync(cancellationToken).ConfigureAwait(false);
        if (mine is null || mine.BranchId != branchId)
        {
            throw new ForbiddenException("A branch manager can only manage the branch they are assigned to.");
        }
    }

    /// <summary>Super administrators, and staff whose assignment covers the warehouse.</summary>
    public async Task EnsureCanWorkAtWarehouseAsync(Warehouse warehouse, CancellationToken cancellationToken)
    {
        if (IsSuperAdmin)
        {
            return;
        }

        var mine = await GetMyAssignmentAsync(cancellationToken).ConfigureAwait(false);
        if (mine is null)
        {
            throw new ForbiddenException("You are not assigned to a branch yet. Ask a manager to assign you before scanning at a warehouse.");
        }

        if (!mine.CoversWarehouse(warehouse.BranchId, warehouse.Id))
        {
            throw new ForbiddenException($"You are not assigned to '{warehouse.Name}'. Ask a manager to assign you to it.");
        }
    }
}
