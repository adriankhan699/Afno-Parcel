using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.Application.Network;

/// <summary>
/// Warehouse scanning. A scan accepts whatever a barcode or QR scanner read, finds the parcel, and records an arrival, a
/// departure or a hand-over to a rider. Every scan is logged with the parcel, warehouse, staff member, type, time and
/// remarks. Scanning the same thing twice is safe: the repeat is logged as a duplicate and creates no second movement.
/// </summary>
internal sealed partial class ScanService(
    IParcelRepository parcels,
    IMovementRepository movements,
    IWarehouseRepository warehouses,
    ParcelStatusChanger changer,
    WorkplaceAccess access,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider clock,
    IValidator<ScanRequest> scanValidator,
    IValidator<BatchScanRequest> batchValidator,
    IValidator<ScanLogQuery> logValidator,
    ILogger<ScanService> logger) : IScanService
{
    public async Task<ScanResultDto> ScanAsync(ScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await scanValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var scanType = Enum.Parse<ScanType>(request.ScanType, ignoreCase: true);
        EnsureMayScan(scanType);
        return await ScanOneAsync(request.TrackingNumber, scanType, request.WarehouseId, request.ToWarehouseId, request.Remarks, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BatchScanResponse> ScanBatchAsync(BatchScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await batchValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var scanType = Enum.Parse<ScanType>(request.ScanType, ignoreCase: true);
        EnsureMayScan(scanType);

        // The warehouse is the same for every parcel, so check it once. A bad warehouse or an unauthorised person fails the whole request.
        var warehouse = await changer.GetActiveWarehouseAsync(request.WarehouseId, cancellationToken).ConfigureAwait(false);
        await access.EnsureCanWorkAtWarehouseAsync(warehouse, cancellationToken).ConfigureAwait(false);

        var results = new List<ScanResultDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in request.TrackingNumbers.Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            // The same parcel listed twice (or as a barcode and a QR link) is scanned once.
            var key = TrackingNumbers.TryExtract(raw, out var number) ? number : TrackingNumbers.Normalize(raw);
            if (!seen.Add(key))
            {
                continue;
            }

            try
            {
                results.Add(await ScanOneAsync(raw, scanType, request.WarehouseId, request.ToWarehouseId, request.Remarks, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is AppException or DomainException)
            {
                // A failed scan changes nothing: drop whatever was staged for it and carry on with the next parcel.
                unitOfWork.DiscardPendingChanges();
                results.Add(new ScanResultDto(key, "Failed", null, null, null, null, null, null, ex.Message));
            }
        }

        var applied = results.Count(r => r.Outcome == "Applied");
        var duplicates = results.Count(r => r.Outcome == "Duplicate");
        LogBatch(logger, scanType, applied, duplicates, results.Count - applied - duplicates);
        return new BatchScanResponse(results, applied, duplicates, results.Count - applied - duplicates);
    }

    public async Task<PagedResponse<ParcelScanResponse>> SearchAsync(ScanLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await logValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        string? tracking = null;
        if (!string.IsNullOrWhiteSpace(query.TrackingNumber))
        {
            tracking = TrackingNumbers.TryExtract(query.TrackingNumber, out var number) ? number : TrackingNumbers.Normalize(query.TrackingNumber);
        }

        var filter = new ScanLogFilter(
            query.WarehouseId,
            tracking,
            string.IsNullOrWhiteSpace(query.ScanType) ? null : Enum.Parse<ScanType>(query.ScanType, ignoreCase: true),
            string.IsNullOrWhiteSpace(query.Outcome) ? null : Enum.Parse<ScanOutcome>(query.Outcome, ignoreCase: true),
            query.StaffUserId,
            query.From is { } from ? StartOfNepalDay(from) : null,
            query.To is { } to ? StartOfNepalDay(to.AddDays(1)) : null);

        var (items, total) = await movements.SearchScansAsync(filter, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return new PagedResponse<ParcelScanResponse>(
            items.Select(i => new ParcelScanResponse(
                i.Id, i.ParcelId, i.TrackingNumber, i.WarehouseId, i.WarehouseName, i.StaffUserId, i.StaffName,
                i.ScanType.ToString(), i.Outcome.ToString(), i.ScannedAtUtc, i.Remarks, i.MovementId)).ToList(),
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<IReadOnlyList<ParcelMovementResponse>> GetMovementsAsync(Guid parcelId, CancellationToken cancellationToken = default)
    {
        if (await parcels.GetByIdAsync(parcelId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new NotFoundException("Parcel", parcelId);
        }

        var rows = await movements.GetMovementsAsync(parcelId, cancellationToken).ConfigureAwait(false);
        var ids = rows.SelectMany(m => new[] { m.WarehouseId, m.ToWarehouseId ?? Guid.Empty }).Where(i => i != Guid.Empty).Distinct().ToList();
        var names = ids.Count == 0
            ? new Dictionary<Guid, string>()
            : (await warehouses.GetByIdsAsync(ids, cancellationToken).ConfigureAwait(false)).ToDictionary(kv => kv.Key, kv => kv.Value.Name);

        return rows
            .OrderBy(m => m.OccurredAtUtc)
            .Select(m => new ParcelMovementResponse(
                m.Id,
                m.ScanType.ToString(),
                m.WarehouseId,
                names.GetValueOrDefault(m.WarehouseId, "Unknown warehouse"),
                m.ToWarehouseId,
                m.ToWarehouseId is { } to ? names.GetValueOrDefault(to, "Unknown warehouse") : null,
                m.StaffName,
                m.OccurredAtUtc,
                m.Remarks,
                m.IsUnexpectedArrival))
            .ToList();
    }

    private async Task<ScanResultDto> ScanOneAsync(
        string rawCode,
        ScanType scanType,
        Guid warehouseId,
        Guid? toWarehouseId,
        string? remarks,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException("Authentication is required.");

        if (!TrackingNumbers.TryExtract(rawCode, out var trackingNumber))
        {
            throw new NotFoundException("Parcel", TrackingNumbers.Normalize(rawCode));
        }

        var parcel = await parcels.GetByTrackingNumberAsync(trackingNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", trackingNumber);
        var warehouse = await changer.GetActiveWarehouseAsync(warehouseId, cancellationToken).ConfigureAwait(false);
        await access.EnsureCanWorkAtWarehouseAsync(warehouse, cancellationToken).ConfigureAwait(false);

        var target = scanType.ToStatus();
        var now = clock.GetUtcNow();

        // The same scan again: the parcel is already in the state this scan produces, at this warehouse. Log it, change nothing.
        var latest = await movements.GetLatestMovementAsync(parcel.Id, cancellationToken).ConfigureAwait(false);
        if (latest is not null && latest.ScanType == scanType && latest.WarehouseId == warehouse.Id && parcel.Status == target)
        {
            movements.AddScan(ParcelScan.Create(parcel.Id, warehouse.Id, userId, currentUser.UserName, scanType, now, ScanOutcome.Duplicate, latest.Id, remarks));
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            LogDuplicate(logger, parcel.Id, scanType, warehouse.Id);
            return new ScanResultDto(
                parcel.TrackingNumber,
                "Duplicate",
                parcel.Status.ToString(),
                ParcelStatusCatalog.Get(parcel.Status).Name,
                $"Already recorded: {DescribeScan(scanType)} at {warehouse.Name} on {NepalTime.Format(latest.OccurredAtUtc)}. Nothing was changed.",
                warehouse.Name,
                null,
                latest.OccurredAtUtc);
        }

        var change = await changer.ApplyAsync(parcel, target, remarks, null, warehouse.Id, toWarehouseId, cancellationToken).ConfigureAwait(false);
        var movement = change.Movement ?? throw new DomainException("This scan did not produce a warehouse movement.");
        movements.AddScan(ParcelScan.Create(parcel.Id, warehouse.Id, userId, currentUser.UserName, scanType, now, ScanOutcome.Applied, movement.Id, remarks));
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogApplied(logger, parcel.Id, scanType, warehouse.Id);
        return new ScanResultDto(
            parcel.TrackingNumber,
            "Applied",
            target.ToString(),
            ParcelStatusCatalog.Get(target).Name,
            movement.IsUnexpectedArrival
                ? $"Recorded, but this parcel was dispatched to a different warehouse than {warehouse.Name}."
                : $"{DescribeScan(scanType)} recorded at {warehouse.Name}.",
            warehouse.Name,
            change.ToLocation,
            movement.OccurredAtUtc);
    }

    private void EnsureMayScan(ScanType scanType)
    {
        if (currentUser.UserId is null)
        {
            throw new UnauthorizedException("Authentication is required.");
        }

        var target = scanType.ToStatus();
        if (!StatusAccess.CanSet(currentUser, target))
        {
            throw new ForbiddenException($"Your role is not allowed to record '{ParcelStatusCatalog.Get(target).Name}' scans.");
        }
    }

    private static string DescribeScan(ScanType scanType) => scanType switch
    {
        ScanType.Arrival => "Arrival",
        ScanType.Dispatch => "Dispatch",
        _ => "Hand-over to a rider",
    };

    private static DateTimeOffset StartOfNepalDay(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), NepalTime.Offset).ToUniversalTime();

    [LoggerMessage(EventId = 9201, Level = LogLevel.Information, Message = "Parcel {ParcelId}: {ScanType} scan recorded at warehouse {WarehouseId}")]
    private static partial void LogApplied(ILogger logger, Guid parcelId, ScanType scanType, Guid warehouseId);

    [LoggerMessage(EventId = 9202, Level = LogLevel.Information, Message = "Parcel {ParcelId}: repeated {ScanType} scan at warehouse {WarehouseId} ignored")]
    private static partial void LogDuplicate(ILogger logger, Guid parcelId, ScanType scanType, Guid warehouseId);

    [LoggerMessage(EventId = 9203, Level = LogLevel.Information, Message = "Batch {ScanType} scan: {Applied} applied, {Duplicates} duplicate, {Failed} failed")]
    private static partial void LogBatch(ILogger logger, ScanType scanType, int applied, int duplicates, int failed);
}
