using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Staff;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;

namespace NepalParcel.Application.Network;

/// <summary>Who works where. A person has one active assignment; changing it keeps the old one as history.</summary>
internal sealed partial class StaffService(
    IIdentityService identity,
    IStaffAssignmentRepository assignments,
    IBranchRepository branches,
    IWarehouseRepository warehouses,
    WorkplaceAccess access,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IValidator<StaffQuery> queryValidator,
    IValidator<AssignStaffRequest> assignValidator,
    ILogger<StaffService> logger) : IStaffService
{
    public async Task<PagedResponse<StaffMemberResponse>> ListAsync(StaffQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await queryValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        IReadOnlyCollection<Guid>? only = null;
        IReadOnlyCollection<Guid>? exclude = null;
        if (query.BranchId is not null || query.WarehouseId is not null)
        {
            only = await assignments.GetActiveUserIdsAsync(query.BranchId, query.WarehouseId, cancellationToken).ConfigureAwait(false);
        }
        else if (query.Unassigned is not null)
        {
            var assigned = await assignments.GetActiveUserIdsAsync(null, null, cancellationToken).ConfigureAwait(false);
            if (query.Unassigned == true)
            {
                exclude = assigned;
            }
            else
            {
                only = assigned;
            }
        }

        if (only is { Count: 0 })
        {
            return new PagedResponse<StaffMemberResponse>([], query.Page, query.PageSize, 0);
        }

        if (query.Unassigned == true && only is not null)
        {
            // "Unassigned" together with a branch makes no sense; nobody is both.
            return new PagedResponse<StaffMemberResponse>([], query.Page, query.PageSize, 0);
        }

        var (users, total) = await identity
            .ListStaffAsync(new StaffUserFilter(query.Search?.Trim(), query.Role?.Trim(), only, exclude), query.Page, query.PageSize, cancellationToken)
            .ConfigureAwait(false);

        var current = await assignments.GetActiveByUserIdsAsync(users.Select(u => u.Id).ToList(), cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync(current.Values, cancellationToken).ConfigureAwait(false);

        return new PagedResponse<StaffMemberResponse>(
            users.Select(u => ToResponse(u, current.GetValueOrDefault(u.Id), names)).ToList(),
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<StaffMemberResponse> AssignAsync(Guid userId, AssignStaffRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await assignValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var user = await identity.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Staff member", userId);
        if (!user.Roles.Any(r => Roles.Staff.Contains(r, StringComparer.Ordinal)))
        {
            throw new DomainException("Only staff accounts can be assigned to a branch. Customers cannot.");
        }

        if (!user.IsActive)
        {
            throw new DomainException("This account is switched off, so it cannot be assigned.");
        }

        var branch = await branches.GetByIdAsync(request.BranchId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Branch", request.BranchId);
        if (!branch.IsActive)
        {
            throw new DomainException("Staff cannot be assigned to a branch that is not active.");
        }

        string? warehouseName = null;
        if (request.WarehouseId is { } warehouseId)
        {
            var warehouse = await warehouses.GetByIdAsync(warehouseId, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException("Warehouse", warehouseId);
            if (!warehouse.IsActive)
            {
                throw new DomainException("Staff cannot be assigned to a warehouse that is not active.");
            }

            if (warehouse.BranchId != branch.Id)
            {
                throw new DomainException("That warehouse does not belong to the chosen branch.");
            }

            warehouseName = warehouse.Name;
        }

        await access.EnsureCanManageBranchAsync(branch.Id, cancellationToken).ConfigureAwait(false);

        var existing = await assignments.GetActiveByUserIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!access.IsSuperAdmin)
        {
            if (user.Roles.Contains(Roles.SuperAdmin, StringComparer.Ordinal))
            {
                throw new ForbiddenException("Only a super administrator can change a super administrator's assignment.");
            }

            if (existing is not null && existing.BranchId != branch.Id)
            {
                throw new ForbiddenException("Only a super administrator can move staff from one branch to another.");
            }
        }

        if (existing is not null && existing.BranchId == branch.Id && existing.WarehouseId == request.WarehouseId)
        {
            return ToResponse(user, existing, new Dictionary<Guid, string> { [branch.Id] = branch.Name });
        }

        var now = clock.GetUtcNow();
        if (existing is not null)
        {
            // End the old assignment first: only one can be active at a time.
            existing.End(now, currentUser.UserId);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var created = StaffAssignment.Create(userId, branch.Id, request.WarehouseId, now, currentUser.UserId);
        assignments.Add(created);
        AddAudit(AuditActions.StaffAssigned, userId, now, new { branchId = branch.Id, warehouseId = request.WarehouseId });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogAssigned(logger, userId, branch.Id, currentUser.UserId);
        var names = new Dictionary<Guid, string> { [branch.Id] = branch.Name };
        if (request.WarehouseId is { } w && warehouseName is not null)
        {
            names[w] = warehouseName;
        }

        return ToResponse(user, created, names);
    }

    public async Task UnassignAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var current = await assignments.GetActiveByUserIdAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Assignment for staff member", userId);
        await access.EnsureCanManageBranchAsync(current.BranchId, cancellationToken).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        current.End(now, currentUser.UserId);
        AddAudit(AuditActions.StaffUnassigned, userId, now, new { branchId = current.BranchId, warehouseId = current.WarehouseId });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogUnassigned(logger, userId, currentUser.UserId);
    }

    public async Task<MyAssignmentResponse> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var mine = await access.GetMyAssignmentAsync(cancellationToken).ConfigureAwait(false);
        if (mine is null)
        {
            return new MyAssignmentResponse(null);
        }

        var names = await NamesAsync([mine], cancellationToken).ConfigureAwait(false);
        return new MyAssignmentResponse(ToDto(mine, names));
    }

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IEnumerable<StaffAssignment> list, CancellationToken cancellationToken)
    {
        var all = list.ToList();
        var names = new Dictionary<Guid, string>();
        var branchIds = all.Select(a => a.BranchId).Distinct().ToList();
        var warehouseIds = all.Select(a => a.WarehouseId).OfType<Guid>().Distinct().ToList();

        if (branchIds.Count > 0)
        {
            foreach (var (id, branch) in await branches.GetByIdsAsync(branchIds, cancellationToken).ConfigureAwait(false))
            {
                names[id] = branch.Name;
            }
        }

        if (warehouseIds.Count > 0)
        {
            foreach (var (id, warehouse) in await warehouses.GetByIdsAsync(warehouseIds, cancellationToken).ConfigureAwait(false))
            {
                names[id] = warehouse.Name;
            }
        }

        return names;
    }

    private static StaffAssignmentDto ToDto(StaffAssignment a, IReadOnlyDictionary<Guid, string> names) =>
        new(
            a.Id,
            a.BranchId,
            names.GetValueOrDefault(a.BranchId, "Unknown branch"),
            a.WarehouseId,
            a.WarehouseId is { } w ? names.GetValueOrDefault(w, "Unknown warehouse") : null,
            a.AssignedAtUtc);

    private static StaffMemberResponse ToResponse(UserInfo user, StaffAssignment? assignment, IReadOnlyDictionary<Guid, string> names) =>
        new(user.Id, user.FullName, user.Email, user.Roles, user.IsActive, assignment is null ? null : ToDto(assignment, names));

    private void AddAudit(string action, Guid targetUserId, DateTimeOffset now, object values) =>
        auditLogs.Add(AuditLog.Create(
            action,
            "User",
            targetUserId.ToString(),
            currentUser.UserId,
            now,
            newValues: JsonSerializer.Serialize(values),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));

    [LoggerMessage(EventId = 9301, Level = LogLevel.Information, Message = "Staff {UserId} assigned to branch {BranchId} by {ActorId}")]
    private static partial void LogAssigned(ILogger logger, Guid userId, Guid branchId, Guid? actorId);

    [LoggerMessage(EventId = 9302, Level = LogLevel.Information, Message = "Staff {UserId} unassigned by {ActorId}")]
    private static partial void LogUnassigned(ILogger logger, Guid userId, Guid? actorId);
}
