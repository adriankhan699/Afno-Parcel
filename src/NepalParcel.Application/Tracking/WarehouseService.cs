using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Mapping;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Network;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.Application.Tracking;

internal static class WarehouseMappings
{
    public static WarehouseResponse ToResponse(this Warehouse w, string branchName = "", int held = 0, int inbound = 0) =>
        new(w.Id, w.BranchId, w.Code, w.Name, w.Address.ToDto(), w.PhoneNumber, w.CapacityParcels, w.IsActive, branchName, held, inbound);
}

internal sealed partial class WarehouseService(
    IWarehouseRepository warehouses,
    IBranchRepository branches,
    IInventoryRepository inventory,
    WorkplaceAccess access,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IValidator<CreateWarehouseRequest> createValidator,
    IValidator<UpdateWarehouseRequest> updateValidator,
    IValidator<WarehouseQuery> queryValidator,
    ILogger<WarehouseService> logger) : IWarehouseService
{
    public async Task<PagedResponse<WarehouseResponse>> GetPageAsync(WarehouseQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await queryValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var includeInactive = query.IncludeInactive && Roles.NetworkAdmins.Any(currentUser.IsInRole);
        var filter = new WarehouseFilter(string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(), query.BranchId, includeInactive);
        var (items, total) = await warehouses.SearchAsync(filter, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);

        return new PagedResponse<WarehouseResponse>(
            await ToResponsesAsync(items, cancellationToken).ConfigureAwait(false),
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<WarehouseResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var warehouse = await warehouses.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Warehouse", id);
        return (await ToResponsesAsync([warehouse], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<WarehouseResponse> CreateAsync(CreateWarehouseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await createValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var branch = await branches.GetByIdAsync(request.BranchId, cancellationToken).ConfigureAwait(false);
        if (branch is null || !branch.IsActive)
        {
            throw new NotFoundException("Branch", request.BranchId);
        }

        await access.EnsureCanManageBranchAsync(branch.Id, cancellationToken).ConfigureAwait(false);

        var code = request.Code.Trim().ToUpperInvariant();
        if (await warehouses.CodeExistsAsync(code, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException($"A warehouse with code '{code}' already exists.");
        }

        var warehouse = Warehouse.Create(branch.Id, code, request.Name, request.Address.ToDomain(), request.PhoneNumber, request.CapacityParcels);
        warehouses.Add(warehouse);
        AddAudit(AuditActions.WarehouseCreated, warehouse.Id, new { warehouse.Code, warehouse.Name, branchId = branch.Id });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogCreated(logger, warehouse.Id, warehouse.Code);
        return warehouse.ToResponse(branch.Name);
    }

    public async Task<WarehouseResponse> UpdateAsync(Guid id, UpdateWarehouseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await updateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var warehouse = await warehouses.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Warehouse", id);
        await access.EnsureCanManageBranchAsync(warehouse.BranchId, cancellationToken).ConfigureAwait(false);

        warehouse.Update(request.Name, request.Address.ToDomain(), request.PhoneNumber, request.CapacityParcels);
        AddAudit(AuditActions.WarehouseUpdated, warehouse.Id, new { warehouse.Code, warehouse.Name });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (await ToResponsesAsync([warehouse], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<WarehouseResponse> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var warehouse = await warehouses.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Warehouse", id);
        await access.EnsureCanManageBranchAsync(warehouse.BranchId, cancellationToken).ConfigureAwait(false);

        if (warehouse.IsActive)
        {
            throw new ConflictException("This warehouse is already active.");
        }

        var branch = await branches.GetByIdAsync(warehouse.BranchId, cancellationToken).ConfigureAwait(false);
        if (branch is null || !branch.IsActive)
        {
            throw new DomainException("Its branch is not active. Activate the branch first.");
        }

        warehouse.Activate();
        AddAudit(AuditActions.WarehouseActivated, warehouse.Id, new { warehouse.Code });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (await ToResponsesAsync([warehouse], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<WarehouseResponse> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var warehouse = await warehouses.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Warehouse", id);
        await access.EnsureCanManageBranchAsync(warehouse.BranchId, cancellationToken).ConfigureAwait(false);

        if (!warehouse.IsActive)
        {
            throw new ConflictException("This warehouse is already inactive.");
        }

        var held = (await inventory.CountHeldByWarehouseAsync([id], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(id);
        if (held > 0)
        {
            throw new DomainException($"This warehouse still holds {held} parcel(s). Dispatch them before deactivating it.");
        }

        var inbound = (await inventory.CountInboundByWarehouseAsync([id], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(id);
        if (inbound > 0)
        {
            throw new DomainException($"{inbound} parcel(s) are on their way to this warehouse. Wait for them to arrive or redirect them first.");
        }

        warehouse.Deactivate();
        AddAudit(AuditActions.WarehouseDeactivated, warehouse.Id, new { warehouse.Code });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (await ToResponsesAsync([warehouse], cancellationToken).ConfigureAwait(false))[0];
    }

    private async Task<IReadOnlyList<WarehouseResponse>> ToResponsesAsync(IReadOnlyList<Warehouse> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var ids = items.Select(w => w.Id).ToList();
        var held = await inventory.CountHeldByWarehouseAsync(ids, cancellationToken).ConfigureAwait(false);
        var inbound = await inventory.CountInboundByWarehouseAsync(ids, cancellationToken).ConfigureAwait(false);
        var branchNames = (await branches.GetByIdsAsync(items.Select(w => w.BranchId).Distinct().ToList(), cancellationToken).ConfigureAwait(false))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Name);

        return items
            .Select(w => w.ToResponse(branchNames.GetValueOrDefault(w.BranchId, string.Empty), held.GetValueOrDefault(w.Id), inbound.GetValueOrDefault(w.Id)))
            .ToList();
    }

    private void AddAudit(string action, Guid warehouseId, object values) =>
        auditLogs.Add(AuditLog.Create(
            action,
            "Warehouse",
            warehouseId.ToString(),
            currentUser.UserId,
            clock.GetUtcNow(),
            newValues: JsonSerializer.Serialize(values),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "Warehouse {WarehouseId} created with code {WarehouseCode}")]
    private static partial void LogCreated(ILogger logger, Guid warehouseId, string warehouseCode);
}
