namespace NepalParcel.Contracts.Tracking;

/// <summary>
/// Moves a parcel to a new status. Status is one of PickedUp, Shipped, InTransit, ArrivedAtWarehouse, OutForDelivery,
/// Delivered, DeliveryFailed or Returned (BookingCreated is automatic; cancelling has its own endpoint).
/// Remarks are required for DeliveryFailed and Returned. Shipped needs a branch, ArrivedAtWarehouse needs a warehouse,
/// OutForDelivery and Returned need a branch or a warehouse (never both).
/// </summary>
public sealed record UpdateParcelStatusRequest(
    string Status,
    string? Remarks = null,
    Guid? BranchId = null,
    Guid? WarehouseId = null);

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
/// One warehouse scan. ScanType is Arrival (parcel arrives: ArrivedAtWarehouse), Dispatch (parcel leaves for the next hub:
/// InTransit) or OutForDelivery (handed to a rider).
/// </summary>
public sealed record ScanRequest(string TrackingNumber, string ScanType, Guid WarehouseId, string? Remarks = null);

/// <summary>Several scans of the same type at the same warehouse (1–100 tracking numbers).</summary>
public sealed record BatchScanRequest(
    string ScanType,
    Guid WarehouseId,
    IReadOnlyList<string> TrackingNumbers,
    string? Remarks = null);

/// <summary>The outcome of one scan. A failed scan changes nothing.</summary>
public sealed record ScanResultDto(string TrackingNumber, bool Succeeded, string? Status, string? StatusName, string? Error);

public sealed record BatchScanResponse(IReadOnlyList<ScanResultDto> Results, int Succeeded, int Failed);

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
