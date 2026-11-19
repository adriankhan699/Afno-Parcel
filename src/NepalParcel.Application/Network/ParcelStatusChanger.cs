using System.Text.Json;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Delivery;
using NepalParcel.Application.Notifications;
using NepalParcel.Application.Tracking;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.Application.Network;

/// <param name="Movement">The arrival or departure recorded at a warehouse, if this change happened at one.</param>
internal sealed record StatusChange(
    ParcelStatusCode Previous,
    ParcelStatusHistory History,
    ParcelMovement? Movement,
    string? Location,
    string? ToLocation);

/// <summary>
/// The one place a parcel's status changes, whether staff use the status API or scan a barcode. It checks where the change
/// happened (branch or warehouse must exist and be active, and the person must work there), moves the parcel along its
/// workflow, appends the history row, records the warehouse movement, keeps the parcel's delivery assignment in step with
/// its status, and writes the audit entry. It stages everything and leaves the saving to the caller, so a scan can add its own
/// log row in the same transaction.
/// </summary>
internal sealed class ParcelStatusChanger(
    IBranchRepository branches,
    IWarehouseRepository warehouses,
    IMovementRepository movements,
    IDeliveryAssignmentRepository deliveries,
    IAuditLogRepository auditLogs,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    WorkplaceAccess access,
    INotificationPlanner notifications)
{
    public static ScanType? ScanTypeFor(ParcelStatusCode target) => target switch
    {
        ParcelStatusCode.ArrivedAtWarehouse => ScanType.Arrival,
        ParcelStatusCode.InTransit => ScanType.Dispatch,
        ParcelStatusCode.OutForDelivery => ScanType.OutForDelivery,
        _ => null,
    };

    public async Task<StatusChange> ApplyAsync(
        Parcel parcel,
        ParcelStatusCode target,
        string? remarks,
        Guid? branchId,
        Guid? warehouseId,
        Guid? toWarehouseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        var userId = currentUser.UserId ?? throw new UnauthorizedException("Authentication is required.");

        if (toWarehouseId is not null && (target != ParcelStatusCode.InTransit || warehouseId is null))
        {
            throw new DomainException("A destination warehouse can only be given when a parcel leaves a warehouse in transit.");
        }

        string? location = null;
        string? toLocation = null;
        Warehouse? warehouse = null;

        if (branchId is { } branch)
        {
            var found = await branches.GetByIdAsync(branch, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException("Branch", branch);
            if (!found.IsActive)
            {
                throw new DomainException("The selected branch is not active.");
            }

            location = found.Name;
        }

        if (warehouseId is { } warehouseKey)
        {
            warehouse = await GetActiveWarehouseAsync(warehouseKey, cancellationToken).ConfigureAwait(false);
            await access.EnsureCanWorkAtWarehouseAsync(warehouse, cancellationToken).ConfigureAwait(false);
            location = warehouse.Name;
        }

        if (toWarehouseId is { } destinationKey)
        {
            if (destinationKey == warehouseId)
            {
                throw new DomainException("A parcel cannot be dispatched to the warehouse it is leaving.");
            }

            toLocation = (await GetActiveWarehouseAsync(destinationKey, cancellationToken).ConfigureAwait(false)).Name;
        }

        var previous = parcel.Status;
        var expectedWarehouse = parcel.NextWarehouseId;
        var now = clock.GetUtcNow();

        var history = parcel.ChangeStatus(target, now, userId, currentUser.UserName, remarks, branchId, warehouseId);
        if (target == ParcelStatusCode.InTransit)
        {
            parcel.SetNextWarehouse(toWarehouseId);
        }

        await SyncDeliveryAssignmentAsync(parcel, target, remarks, now, cancellationToken).ConfigureAwait(false);

        ParcelMovement? movement = null;
        if (warehouse is not null && ScanTypeFor(target) is { } scanType)
        {
            movement = ParcelMovement.Create(
                parcel.Id,
                warehouse.Id,
                scanType,
                userId,
                currentUser.UserName,
                now,
                remarks,
                scanType == ScanType.Dispatch ? toWarehouseId : null,
                isUnexpectedArrival: scanType == ScanType.Arrival && expectedWarehouse is not null && expectedWarehouse != warehouse.Id);
            movements.AddMovement(movement);
        }

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
                toWarehouseId,
            }),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));

        // Staged in the same transaction as the status change: the notification exists if and only if the change is saved.
        await notifications.PlanAsync(parcel, history, null, cancellationToken).ConfigureAwait(false);

        return new StatusChange(previous, history, movement, location, toLocation);
    }

    /// <summary>
    /// A delivery agent's assignment follows the parcel, whoever changes the status and however they do it: handing the parcel
    /// to a rider starts the assignment, and delivering, failing, returning or cancelling the parcel finishes it. Moving a
    /// parcel between warehouses leaves a waiting assignment alone.
    /// </summary>
    private async Task SyncDeliveryAssignmentAsync(Parcel parcel, ParcelStatusCode target, string? remarks, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (target is not (ParcelStatusCode.OutForDelivery or ParcelStatusCode.Delivered or ParcelStatusCode.DeliveryFailed
            or ParcelStatusCode.Returned or ParcelStatusCode.Cancelled))
        {
            return;
        }

        var open = await deliveries.GetOpenForParcelAsync(parcel.Id, AssignmentType.Delivery, cancellationToken).ConfigureAwait(false);
        if (open is null)
        {
            return;
        }

        switch (target)
        {
            case ParcelStatusCode.OutForDelivery:
                if (open.Status != AssignmentStatus.InProgress)
                {
                    open.Start(now);
                }

                break;
            case ParcelStatusCode.Delivered:
                if (open.Status != AssignmentStatus.InProgress)
                {
                    open.Start(now);
                }

                open.Complete(now);
                break;
            case ParcelStatusCode.DeliveryFailed:
                if (open.Status != AssignmentStatus.InProgress)
                {
                    open.Start(now);
                }

                open.Fail(now, remarks is { Length: > 300 } ? remarks[..300] : remarks ?? "Delivery failed");
                break;
            default:
                open.Cancel(now);
                break;
        }
    }

    public async Task<Warehouse> GetActiveWarehouseAsync(Guid id, CancellationToken cancellationToken)
    {
        var warehouse = await warehouses.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Warehouse", id);
        if (!warehouse.IsActive)
        {
            throw new DomainException("The selected warehouse is not active.");
        }

        return warehouse;
    }
}
