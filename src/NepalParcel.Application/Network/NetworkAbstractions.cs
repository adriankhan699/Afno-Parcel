using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Contracts.Staff;
using NepalParcel.Contracts.Tracking;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.Application.Network;

/// <summary>Which parcels are waiting for somebody to act on them.</summary>
public enum WaitingKind
{
    /// <summary>Booked and waiting to be collected from, or handed in by, the sender.</summary>
    Pickup = 1,

    /// <summary>Picked up at a branch, or sitting at a warehouse, waiting to be sent on.</summary>
    Dispatch = 2,
}

public sealed record InventoryRow(
    Guid ParcelId,
    string TrackingNumber,
    int StatusId,
    ServiceType ServiceType,
    decimal WeightKg,
    int Quantity,
    string DestinationDistrict,
    DateTimeOffset SinceUtc);

public sealed record WaitingFilter(WaitingKind? Kind, Guid? BranchId, Guid? WarehouseId);

public sealed record WaitingRow(
    Guid ParcelId,
    string TrackingNumber,
    int StatusId,
    string Location,
    ServiceType ServiceType,
    decimal WeightKg,
    int Quantity,
    string DestinationDistrict,
    DateTimeOffset SinceUtc);

public sealed record ScanLogFilter(
    Guid? WarehouseId = null,
    string? TrackingNumber = null,
    ScanType? ScanType = null,
    ScanOutcome? Outcome = null,
    Guid? StaffUserId = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? BeforeUtc = null);

public sealed record ScanLogItem(
    Guid Id,
    Guid ParcelId,
    string TrackingNumber,
    Guid WarehouseId,
    string WarehouseName,
    Guid StaffUserId,
    string? StaffName,
    ScanType ScanType,
    ScanOutcome Outcome,
    DateTimeOffset ScannedAtUtc,
    string? Remarks,
    Guid MovementId);

public sealed record StaffUserFilter(
    string? Search = null,
    string? Role = null,
    IReadOnlyCollection<Guid>? OnlyUserIds = null,
    IReadOnlyCollection<Guid>? ExcludeUserIds = null);

public interface IStaffAssignmentRepository
{
    void Add(StaffAssignment assignment);

    /// <summary>The person's current assignment (tracked, so it can be ended), or null.</summary>
    Task<StaffAssignment?> GetActiveByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, StaffAssignment>> GetActiveByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>Ids of people with an active assignment, optionally only at one branch and/or warehouse.</summary>
    Task<IReadOnlyList<Guid>> GetActiveUserIdsAsync(Guid? branchId, Guid? warehouseId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, int>> CountActiveByBranchAsync(IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken = default);
}

public interface IMovementRepository
{
    void AddMovement(ParcelMovement movement);

    void AddScan(ParcelScan scan);

    Task<ParcelMovement?> GetLatestMovementAsync(Guid parcelId, CancellationToken cancellationToken = default);

    /// <summary>Oldest first.</summary>
    Task<IReadOnlyList<ParcelMovement>> GetMovementsAsync(Guid parcelId, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<(IReadOnlyList<ScanLogItem> Items, int TotalCount)> SearchScansAsync(ScanLogFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}

public interface IInventoryRepository
{
    Task<IReadOnlyDictionary<Guid, int>> CountHeldByWarehouseAsync(IReadOnlyCollection<Guid> warehouseIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, int>> CountInboundByWarehouseAsync(IReadOnlyCollection<Guid> warehouseIds, CancellationToken cancellationToken = default);

    /// <summary>Open parcels (booked, picked up or shipped) that are at, or still to be handed in at, the branch.</summary>
    Task<int> CountOpenAtBranchAsync(Guid branchId, CancellationToken cancellationToken = default);

    /// <summary>Longest-waiting first.</summary>
    Task<(IReadOnlyList<InventoryRow> Items, int TotalCount)> GetHeldAsync(Guid warehouseId, string? search, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InventoryRow>> GetInboundAsync(Guid warehouseId, int take, CancellationToken cancellationToken = default);

    /// <summary>Longest-waiting first.</summary>
    Task<(IReadOnlyList<WaitingRow> Items, int TotalCount)> GetWaitingAsync(WaitingFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}

/// <summary>Draws barcodes and QR codes (implemented in Infrastructure). Both return SVG images.</summary>
public interface ILabelGenerator
{
    /// <summary>A Code 128 barcode of the text.</summary>
    string CreateBarcodeSvg(string text);

    string CreateQrSvg(string text);
}

public interface IScanService
{
    Task<ScanResultDto> ScanAsync(ScanRequest request, CancellationToken cancellationToken = default);

    Task<BatchScanResponse> ScanBatchAsync(BatchScanRequest request, CancellationToken cancellationToken = default);

    Task<PagedResponse<ParcelScanResponse>> SearchAsync(ScanLogQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ParcelMovementResponse>> GetMovementsAsync(Guid parcelId, CancellationToken cancellationToken = default);
}

public interface IInventoryService
{
    Task<WarehouseInventoryResponse> GetWarehouseInventoryAsync(Guid warehouseId, string? search, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<PagedResponse<WaitingParcelDto>> GetWaitingAsync(WaitingQuery query, CancellationToken cancellationToken = default);
}

public interface IStaffService
{
    Task<PagedResponse<StaffMemberResponse>> ListAsync(StaffQuery query, CancellationToken cancellationToken = default);

    Task<StaffMemberResponse> AssignAsync(Guid userId, AssignStaffRequest request, CancellationToken cancellationToken = default);

    Task UnassignAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<MyAssignmentResponse> GetMineAsync(CancellationToken cancellationToken = default);
}

public interface IParcelLabelService
{
    Task<ParcelLabelResponse> GetLabelAsync(Guid parcelId, CancellationToken cancellationToken = default);
}
