using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Mapping;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.Application.Tracking;

internal static class WarehouseMappings
{
    public static WarehouseResponse ToResponse(this Warehouse w) =>
        new(w.Id, w.BranchId, w.Code, w.Name, w.Address.ToDto(), w.PhoneNumber, w.CapacityParcels, w.IsActive);
}

internal sealed partial class WarehouseService(
    IWarehouseRepository warehouses,
    IBranchRepository branches,
    IUnitOfWork unitOfWork,
    IValidator<CreateWarehouseRequest> createValidator,
    IValidator<PageQuery> pageValidator,
    ILogger<WarehouseService> logger) : IWarehouseService
{
    public async Task<PagedResponse<WarehouseResponse>> GetPageAsync(PageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await pageValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var (items, total) = await warehouses.GetPageAsync(query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return new PagedResponse<WarehouseResponse>(items.Select(w => w.ToResponse()).ToList(), query.Page, query.PageSize, total);
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

        var code = request.Code.Trim().ToUpperInvariant();
        if (await warehouses.CodeExistsAsync(code, cancellationToken).ConfigureAwait(false))
        {
            throw new ConflictException($"A warehouse with code '{code}' already exists.");
        }

        var warehouse = Warehouse.Create(branch.Id, code, request.Name, request.Address.ToDomain(), request.PhoneNumber, request.CapacityParcels);
        warehouses.Add(warehouse);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogCreated(logger, warehouse.Id, warehouse.Code);
        return warehouse.ToResponse();
    }

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "Warehouse {WarehouseId} created with code {WarehouseCode}")]
    private static partial void LogCreated(ILogger logger, Guid warehouseId, string warehouseCode);
}
