namespace NepalParcel.Contracts.Tracking;

/// <summary>
/// Moves a parcel to a new status. Status is one of PickedUp, Shipped, InTransit, ArrivedAtWarehouse, OutForDelivery,
/// Delivered, DeliveryFailed or Returned (BookingCreated is automatic; cancelling has its own endpoint).
/// Remarks are required for DeliveryFailed and Returned. Shipped needs a branch, ArrivedAtWarehouse needs a warehouse,
/// OutForDelivery and Returned need a branch or a warehouse (never both). For InTransit leaving a warehouse, <c>ToWarehouseId</c>
/// names the warehouse the parcel is travelling to.
/// </summary>
public sealed record UpdateParcelStatusRequest(
    string Status,
    string? Remarks = null,
    Guid? BranchId = null,
    Guid? WarehouseId = null,
    Guid? ToWarehouseId = null);

/// <summary>The result of a status change. Contains no customer details, so riders and scanners can use it freely.</summary>
public sealed record ParcelStatusUpdateResponse(
    Guid ParcelId,
    string TrackingNumber,
    string PreviousStatus,
    string PreviousStatusName,
    string Status,
    string StatusName,
    DateTimeOffset OccurredAtUtc,
    string? Location,
    IReadOnlyList<string> NextStatuses);

/// <summary>
/// One warehouse scan. <c>TrackingNumber</c> is whatever the scanner read: the tracking number from a barcode, or the
/// tracking link from a QR code (the number is extracted). <c>ScanType</c> is Arrival (the parcel arrives: ArrivedAtWarehouse),
/// Dispatch (the parcel leaves for the next warehouse: InTransit; give <c>ToWarehouseId</c> when the destination warehouse is
/// known) or OutForDelivery (handed to a rider). Scanning the same thing twice never creates a second movement.
/// </summary>
public sealed record ScanRequest(
    string TrackingNumber,
    string ScanType,
    Guid WarehouseId,
    string? Remarks = null,
    Guid? ToWarehouseId = null);

/// <summary>Several scans of the same type at the same warehouse (1–100 codes).</summary>
public sealed record BatchScanRequest(
    string ScanType,
    Guid WarehouseId,
    IReadOnlyList<string> TrackingNumbers,
    string? Remarks = null,
    Guid? ToWarehouseId = null);

/// <summary>
/// The outcome of one scan. <c>Outcome</c> is Applied (a new movement was recorded), Duplicate (the same scan was already
/// recorded: nothing changed) or Failed (refused: nothing changed; see <c>Error</c>). <c>Succeeded</c> is true unless Failed.
/// </summary>
public sealed record ScanResultDto(
    string TrackingNumber,
    string Outcome,
    string? Status,
    string? StatusName,
    string? Message,
    string? Warehouse,
    string? ToWarehouse,
    DateTimeOffset? ScannedAtUtc,
    string? Error = null)
{
    public bool Succeeded => !string.Equals(Outcome, "Failed", StringComparison.Ordinal);
}

/// <summary>
/// The outcome of a batch of scans. Succeeded counts scans that recorded a new movement, Duplicates counts repeats of a scan
/// already recorded, and Failed counts scans that were refused.
/// </summary>
public sealed record BatchScanResponse(IReadOnlyList<ScanResultDto> Results, int Succeeded, int Duplicates, int Failed);

/// <summary>One arrival or departure of a parcel at a warehouse. The movement history is append-only.</summary>
public sealed record ParcelMovementResponse(
    Guid Id,
    string ScanType,
    Guid WarehouseId,
    string WarehouseName,
    Guid? ToWarehouseId,
    string? ToWarehouseName,
    string? StaffName,
    DateTimeOffset OccurredAtUtc,
    string? Remarks,
    bool IsUnexpectedArrival);

/// <summary>A row of the scan log. Every scan is recorded, including repeats (Outcome = Duplicate).</summary>
public sealed record ParcelScanResponse(
    Guid Id,
    Guid ParcelId,
    string TrackingNumber,
    Guid WarehouseId,
    string WarehouseName,
    Guid StaffUserId,
    string? StaffName,
    string ScanType,
    string Outcome,
    DateTimeOffset ScannedAtUtc,
    string? Remarks,
    Guid MovementId);

/// <summary>Filters for the scan log.</summary>
public sealed record ScanLogQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    public Guid? WarehouseId { get; init; }

    /// <summary>A tracking number or the whole text of a scanned code.</summary>
    public string? TrackingNumber { get; init; }

    /// <summary>Arrival, Dispatch or OutForDelivery.</summary>
    public string? ScanType { get; init; }

    /// <summary>Applied or Duplicate.</summary>
    public string? Outcome { get; init; }

    public Guid? StaffUserId { get; init; }

    /// <summary>First day (inclusive), as a Nepal-time calendar date.</summary>
    public DateOnly? From { get; init; }

    /// <summary>Last day (inclusive), as a Nepal-time calendar date.</summary>
    public DateOnly? To { get; init; }
}

/// <summary>One step of the public timeline: a standard sentence and a place name, never staff remarks or names.</summary>
public sealed record TrackingEventDto(string Status, string StatusName, string Message, DateTimeOffset OccurredAtUtc, string? Location);

/// <summary>
/// What anyone with a tracking number may see. Deliberately contains no names, phone numbers, street addresses, amounts
/// or free-text remarks: only the status, the districts the parcel travels between, and the timeline.
/// </summary>
public sealed record PublicTrackingResponse(
    string TrackingNumber,
    string Status,
    string StatusName,
    string StatusMessage,
    string ServiceType,
    string OriginArea,
    string DestinationArea,
    DateTimeOffset BookedAtUtc,
    DateTimeOffset? EstimatedDeliveryAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    bool IsFinal,
    IReadOnlyList<TrackingEventDto> Events);

/// <summary>A timeline step in the detailed view. Remarks are present only for signed-in callers, never for receivers verified by code.</summary>
public sealed record DetailedTrackingEventDto(
    string Status,
    string StatusName,
    string Message,
    DateTimeOffset OccurredAtUtc,
    string? Location,
    string? Remarks);

/// <summary>
/// Detailed tracking for the receiver (verified with a one-time code) or a signed-in sender or staff member. Still
/// excludes phone numbers, the sender, street and landmark, and staff identities.
/// </summary>
public sealed record DetailedTrackingResponse(
    string TrackingNumber,
    string Status,
    string StatusName,
    string StatusMessage,
    string ServiceType,
    string OriginArea,
    string DestinationArea,
    string ReceiverName,
    string ReceiverArea,
    decimal CodAmount,
    string PaymentMethod,
    DateTimeOffset BookedAtUtc,
    DateTimeOffset? EstimatedDeliveryAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    bool IsFinal,
    IReadOnlyList<DetailedTrackingEventDto> Events);

/// <summary>
/// Always the same answer, whether or not the tracking number exists, so the endpoint cannot be used to find valid numbers
/// or to learn anything about a receiver.
/// </summary>
public sealed record TrackingOtpRequestedResponse(string Message, int ValidForMinutes);

/// <summary>The 6-digit code sent to the receiver.</summary>
public sealed record VerifyTrackingOtpRequest(string Code);

/// <summary>
/// A short-lived token for one tracking number. Send it as the <c>X-Tracking-Access</c> header to
/// <c>GET /api/v1/tracking/{trackingNumber}/details</c>.
/// </summary>
public sealed record TrackingAccessResponse(string AccessToken, DateTimeOffset ExpiresAtUtc);
