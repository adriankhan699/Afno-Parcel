using FluentValidation;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.Application.Network;

/// <summary>What each warehouse holds and expects, and which parcels are waiting for pickup or dispatch.</summary>
internal sealed class InventoryService(
    IInventoryRepository inventory,
    IWarehouseRepository warehouses,
    TimeProvider clock,
    IValidator<WaitingQuery> waitingValidator) : IInventoryService
{
    private const int InboundShown = 50;

    public async Task<WarehouseInventoryResponse> GetWarehouseInventoryAsync(
        Guid warehouseId,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > PageQuery.MaxPageSize)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["Paging"] = [$"Page must be at least 1 and the page size between 1 and {PageQuery.MaxPageSize}."],
            });
        }

        var warehouse = await warehouses.GetByIdAsync(warehouseId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Warehouse", warehouseId);

        var now = clock.GetUtcNow();
        var (held, total) = await inventory.GetHeldAsync(warehouseId, search?.Trim(), page, pageSize, cancellationToken).ConfigureAwait(false);
        var inbound = await inventory.GetInboundAsync(warehouseId, InboundShown, cancellationToken).ConfigureAwait(false);
        var inboundCount = (await inventory.CountInboundByWarehouseAsync([warehouseId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(warehouseId);
        var heldCount = (await inventory.CountHeldByWarehouseAsync([warehouseId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(warehouseId);

        decimal? utilisation = warehouse.CapacityParcels is { } capacity
            ? decimal.Round(heldCount * 100m / capacity, 1, MidpointRounding.AwayFromZero)
            : null;

        return new WarehouseInventoryResponse(
            warehouse.Id,
            warehouse.Name,
            warehouse.CapacityParcels,
            heldCount,
            inboundCount,
            utilisation,
            new PagedResponse<InventoryParcelDto>(held.Select(r => ToDto(r, now)).ToList(), page, pageSize, total),
            inbound.Select(r => ToDto(r, now)).ToList());
    }

    public async Task<PagedResponse<WaitingParcelDto>> GetWaitingAsync(WaitingQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await waitingValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var kind = string.IsNullOrWhiteSpace(query.Kind) ? (WaitingKind?)null : Enum.Parse<WaitingKind>(query.Kind, ignoreCase: true);
        var (rows, total) = await inventory
            .GetWaitingAsync(new WaitingFilter(kind, query.BranchId, query.WarehouseId), query.Page, query.PageSize, cancellationToken)
            .ConfigureAwait(false);

        var now = clock.GetUtcNow();
        return new PagedResponse<WaitingParcelDto>(
            rows.Select(r => new WaitingParcelDto(
                r.ParcelId,
                r.TrackingNumber,
                (ParcelStatusCode)r.StatusId == ParcelStatusCode.BookingCreated ? nameof(WaitingKind.Pickup) : nameof(WaitingKind.Dispatch),
                ((ParcelStatusCode)r.StatusId).ToString(),
                ParcelMappings.StatusName(r.StatusId),
                r.Location,
                r.ServiceType.ToString(),
                r.WeightKg,
                r.Quantity,
                r.DestinationDistrict,
                r.SinceUtc,
                Hours(now, r.SinceUtc))).ToList(),
            query.Page,
            query.PageSize,
            total);
    }

    private static InventoryParcelDto ToDto(InventoryRow r, DateTimeOffset now) =>
        new(
            r.ParcelId,
            r.TrackingNumber,
            ((ParcelStatusCode)r.StatusId).ToString(),
            ParcelMappings.StatusName(r.StatusId),
            r.ServiceType.ToString(),
            r.WeightKg,
            r.Quantity,
            r.DestinationDistrict,
            r.SinceUtc,
            Hours(now, r.SinceUtc));

    private static decimal Hours(DateTimeOffset now, DateTimeOffset since) =>
        decimal.Round((decimal)Math.Max(0, (now - since).TotalHours), 1, MidpointRounding.AwayFromZero);
}
