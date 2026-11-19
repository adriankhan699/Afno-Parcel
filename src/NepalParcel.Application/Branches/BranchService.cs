using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Mapping;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Network;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;

namespace NepalParcel.Application.Branches;

internal sealed partial class BranchService(
    IBranchRepository repository,
    IWarehouseRepository warehouses,
    IStaffAssignmentRepository staff,
    IInventoryRepository inventory,
    WorkplaceAccess access,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IValidator<CreateBranchRequest> createValidator,
    IValidator<UpdateBranchRequest> updateValidator,
    IValidator<BranchQuery> queryValidator,
    ILogger<BranchService> logger) : IBranchService
{
    public async Task<PagedResponse<BranchResponse>> GetPageAsync(BranchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await queryValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        // Customers and the public only ever see active branches; managers may ask for the inactive ones too.
        var includeInactive = query.IncludeInactive && Roles.NetworkAdmins.Any(currentUser.IsInRole);
        var filter = new BranchFilter(
            string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            string.IsNullOrWhiteSpace(query.Type) ? null : Enum.Parse<BranchType>(query.Type, ignoreCase: true),
            includeInactive);

        var (items, total) = await repository.SearchAsync(filter, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        var counts = await CountsAsync(items.Select(b => b.Id).ToList(), cancellationToken).ConfigureAwait(false);

        return new PagedResponse<BranchResponse>(
            items.Select(b => b.ToResponse(counts.Warehouses.GetValueOrDefault(b.Id), counts.Staff.GetValueOrDefault(b.Id))).ToList(),
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<BranchResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var branch = await repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Branch", id);

        return await ToResponseAsync(branch, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BranchResponse> CreateAsync(CreateBranchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await createValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var code = request.Code.Trim().ToUpperInvariant();
        if (await repository.CodeExistsAsync(code, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException($"A branch with code '{code}' already exists.");
        }

        var branch = Branch.Create(
            code,
            request.Name,
            Enum.Parse<BranchType>(request.Type, ignoreCase: true),
            request.Address.ToDomain(),
            request.PhoneNumber,
            request.Email);

        repository.Add(branch);
        AddAudit(AuditActions.BranchCreated, branch.Id, new { branch.Code, branch.Name });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogBranchCreated(logger, branch.Id, branch.Code);
        return branch.ToResponse();
    }

    public async Task<BranchResponse> UpdateAsync(Guid id, UpdateBranchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await updateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var branch = await repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Branch", id);
        await access.EnsureCanManageBranchAsync(branch.Id, cancellationToken).ConfigureAwait(false);

        branch.Update(request.Name, request.Address.ToDomain(), request.PhoneNumber, request.Email);
        AddAudit(AuditActions.BranchUpdated, branch.Id, new { branch.Code, branch.Name });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await ToResponseAsync(branch, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BranchResponse> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var branch = await repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Branch", id);
        await access.EnsureCanManageBranchAsync(branch.Id, cancellationToken).ConfigureAwait(false);

        if (branch.IsActive)
        {
            throw new ConflictException("This branch is already active.");
        }

        branch.Activate();
        AddAudit(AuditActions.BranchActivated, branch.Id, new { branch.Code });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await ToResponseAsync(branch, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BranchResponse> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var branch = await repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Branch", id);
        await access.EnsureCanManageBranchAsync(branch.Id, cancellationToken).ConfigureAwait(false);

        if (!branch.IsActive)
        {
            throw new ConflictException("This branch is already inactive.");
        }

        var counts = await CountsAsync([branch.Id], cancellationToken).ConfigureAwait(false);
        var activeWarehouses = counts.Warehouses.GetValueOrDefault(branch.Id);
        if (activeWarehouses > 0)
        {
            throw new DomainException($"This branch still operates {activeWarehouses} active warehouse(s). Deactivate or move them first.");
        }

        var openParcels = await inventory.CountOpenAtBranchAsync(branch.Id, cancellationToken).ConfigureAwait(false);
        if (openParcels > 0)
        {
            throw new DomainException($"{openParcels} open parcel(s) are still booked at or held by this branch. Finish or move them first.");
        }

        var assignedStaff = counts.Staff.GetValueOrDefault(branch.Id);
        if (assignedStaff > 0)
        {
            throw new DomainException($"{assignedStaff} staff member(s) are still assigned to this branch. Reassign them first.");
        }

        branch.Deactivate();
        AddAudit(AuditActions.BranchDeactivated, branch.Id, new { branch.Code });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogBranchDeactivated(logger, branch.Id, currentUser.UserId);
        return branch.ToResponse();
    }

    private async Task<BranchResponse> ToResponseAsync(Branch branch, CancellationToken cancellationToken)
    {
        var counts = await CountsAsync([branch.Id], cancellationToken).ConfigureAwait(false);
        return branch.ToResponse(counts.Warehouses.GetValueOrDefault(branch.Id), counts.Staff.GetValueOrDefault(branch.Id));
    }

    private async Task<(IReadOnlyDictionary<Guid, int> Warehouses, IReadOnlyDictionary<Guid, int> Staff)> CountsAsync(
        IReadOnlyCollection<Guid> branchIds,
        CancellationToken cancellationToken)
    {
        if (branchIds.Count == 0)
        {
            return (new Dictionary<Guid, int>(), new Dictionary<Guid, int>());
        }

        var warehouseCounts = await warehouses.CountByBranchAsync(branchIds, activeOnly: true, cancellationToken).ConfigureAwait(false);
        var staffCounts = await staff.CountActiveByBranchAsync(branchIds, cancellationToken).ConfigureAwait(false);
        return (warehouseCounts, staffCounts);
    }

    private void AddAudit(string action, Guid branchId, object values) =>
        auditLogs.Add(AuditLog.Create(
            action,
            "Branch",
            branchId.ToString(),
            currentUser.UserId,
            clock.GetUtcNow(),
            newValues: JsonSerializer.Serialize(values),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Branch {BranchId} created with code {BranchCode}")]
    private static partial void LogBranchCreated(ILogger logger, Guid branchId, string branchCode);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Branch {BranchId} deactivated by {ActorId}")]
    private static partial void LogBranchDeactivated(ILogger logger, Guid branchId, Guid? actorId);
}
