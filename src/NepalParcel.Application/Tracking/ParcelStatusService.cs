using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Parcels;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.Application.Tracking;

/// <summary>
/// The only way a parcel's status changes after booking. Every change goes through <see cref="Parcel.ChangeStatus"/>, which
/// checks the workflow and appends a history row; nothing here edits or deletes history.
/// </summary>
internal sealed partial class ParcelStatusService(
    IParcelRepository parcels,
    IBranchRepository branches,
    IWarehouseRepository warehouses,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IValidator<UpdateParcelStatusRequest> updateValidator,
    IValidator<ScanRequest> scanValidator,
    IValidator<BatchScanRequest> batchValidator,
    ILogger<ParcelStatusService> logger) : IParcelStatusService
{
    public async Task<ParcelStatusUpdateResponse> UpdateAsync(Guid parcelId, UpdateParcelStatusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await updateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var target = Enum.Parse<ParcelStatusCode>(request.Status, ignoreCase: true);
        EnsureMayChangeTo(target);

        var parcel = await parcels.GetByIdAsync(parcelId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", parcelId);
        return await ApplyAsync(parcel, target, request.Remarks, request.BranchId, request.WarehouseId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ParcelStatusUpdateResponse> UpdateByTrackingNumberAsync(string trackingNumber, UpdateParcelStatusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await updateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var target = Enum.Parse<ParcelStatusCode>(request.Status, ignoreCase: true);
        EnsureMayChangeTo(target);

        var parcel = await FindByTrackingNumberAsync(trackingNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", TrackingNumbers.Normalize(trackingNumber));
        return await ApplyAsync(parcel, target, request.Remarks, request.BranchId, request.WarehouseId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ParcelStatusUpdateResponse> ScanAsync(ScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await scanValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var target = Enum.Parse<ScanType>(request.ScanType, ignoreCase: true).ToStatus();
        EnsureMayChangeTo(target);

        var parcel = await FindByTrackingNumberAsync(request.TrackingNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", TrackingNumbers.Normalize(request.TrackingNumber));
        return await ApplyAsync(parcel, target, request.Remarks, null, request.WarehouseId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BatchScanResponse> ScanBatchAsync(BatchScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await batchValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var target = Enum.Parse<ScanType>(request.ScanType, ignoreCase: true).ToStatus();
        EnsureMayChangeTo(target);

        // The warehouse is the same for every parcel, so check it once. A bad warehouse fails the whole request.
        await GetActiveWarehouseAsync(request.WarehouseId, cancellationToken).ConfigureAwait(false);

        var results = new List<ScanResultDto>();
        foreach (var raw in request.TrackingNumbers.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            results.Add(await ScanOneAsync(raw, target, request, cancellationToken).ConfigureAwait(false));
        }

        var succeeded = results.Count(r => r.Succeeded);
        LogBatchScanned(logger, target, succeeded, results.Count - succeeded);
        return new BatchScanResponse(results, succeeded, results.Count - succeeded);
    }

    private async Task<ScanResultDto> ScanOneAsync(string input, ParcelStatusCode target, BatchScanRequest request, CancellationToken cancellationToken)
    {
        var shown = TrackingNumbers.Normalize(input);
        try
        {
            var parcel = await FindByTrackingNumberAsync(input, cancellationToken).ConfigureAwait(false);
            if (parcel is null)
            {
                return new ScanResultDto(shown, false, null, null, "No parcel was found with this tracking number.");
            }

            var done = await ApplyAsync(parcel, target, request.Remarks, null, request.WarehouseId, cancellationToken).ConfigureAwait(false);
            return new ScanResultDto(done.TrackingNumber, true, done.Status, done.StatusName, null);
        }
        catch (Exception ex) when (ex is AppException or DomainException)
        {
            // A failed scan changes nothing: drop whatever was staged for it and carry on with the next parcel.
            unitOfWork.DiscardPendingChanges();
            return new ScanResultDto(shown, false, null, null, ex.Message);
        }
    }

    private async Task<ParcelStatusUpdateResponse> ApplyAsync(
        Parcel parcel,
        ParcelStatusCode target,
        string? remarks,
        Guid? branchId,
        Guid? warehouseId,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException("Authentication is required.");

        string? location = null;
        if (branchId is { } branch)
        {
            var found = await branches.GetByIdAsync(branch, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                throw new NotFoundException("Branch", branch);
            }

            if (!found.IsActive)
            {
                throw new DomainException("The selected branch is not active.");
            }

            location = found.Name;
        }

        if (warehouseId is { } warehouse)
        {
            location = (await GetActiveWarehouseAsync(warehouse, cancellationToken).ConfigureAwait(false)).Name;
        }

        var previous = parcel.Status;
        var now = clock.GetUtcNow();
        var history = parcel.ChangeStatus(target, now, userId, currentUser.UserName, remarks, branchId, warehouseId);

        auditLogs.Add(AuditLog.Create(
            AuditActions.ParcelStatusChanged,
            "Parcel",
            parcel.Id.ToString(),
            userId,
            now,
            newValues: JsonSerializer.Serialize(new
            {
                parcel.TrackingNumber,
                from = previous.ToString(),
                to = target.ToString(),
                branchId,
                warehouseId,
            }),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogChanged(logger, parcel.Id, previous, target);
        return new ParcelStatusUpdateResponse(
            parcel.Id,
            parcel.TrackingNumber,
            previous.ToString(),
            ParcelStatusCatalog.Get(previous).Name,
            target.ToString(),
            ParcelStatusCatalog.Get(target).Name,
            history.OccurredAtUtc,
            location,
            parcel.AllowedNextStatuses().Where(s => s != ParcelStatusCode.Cancelled).Select(s => s.ToString()).ToList());
    }

    /// <summary>Checked before the parcel is even loaded, so a role that may not make this change learns nothing about the parcel.</summary>
    private void EnsureMayChangeTo(ParcelStatusCode target)
    {
        if (currentUser.UserId is null)
        {
            throw new UnauthorizedException("Authentication is required.");
        }

        if (!StatusAccess.CanSet(currentUser, target))
        {
            throw new ForbiddenException($"Your role is not allowed to mark a parcel as '{ParcelStatusCatalog.Get(target).Name}'.");
        }
    }

    private async Task<NepalParcel.Domain.Warehouses.Warehouse> GetActiveWarehouseAsync(Guid id, CancellationToken cancellationToken)
    {
        var warehouse = await warehouses.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Warehouse", id);
        if (!warehouse.IsActive)
        {
            throw new DomainException("The selected warehouse is not active.");
        }

        return warehouse;
    }

    private async Task<Parcel?> FindByTrackingNumberAsync(string input, CancellationToken cancellationToken)
    {
        var normalized = TrackingNumbers.Normalize(input);
        return TrackingNumbers.IsValid(normalized)
            ? await parcels.GetByTrackingNumberAsync(normalized, cancellationToken).ConfigureAwait(false)
            : null;
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information, Message = "Parcel {ParcelId} moved from {From} to {To}")]
    private static partial void LogChanged(ILogger logger, Guid parcelId, ParcelStatusCode from, ParcelStatusCode to);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information, Message = "Batch scan to {To}: {Succeeded} succeeded, {Failed} failed")]
    private static partial void LogBatchScanned(ILogger logger, ParcelStatusCode to, int succeeded, int failed);
}

/// <summary>Checks the signed-in user's roles against <see cref="ParcelStatusPermissions"/>.</summary>
internal static class StatusAccess
{
    public static bool CanSet(ICurrentUser user, ParcelStatusCode to) =>
        Roles.All.Any(role => user.IsInRole(role) && ParcelStatusPermissions.CanSet(role, to));
}
