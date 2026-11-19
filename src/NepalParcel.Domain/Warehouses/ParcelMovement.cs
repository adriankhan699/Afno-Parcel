using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Warehouses;

/// <summary>The kinds of warehouse scan, and what each one means for the parcel.</summary>
public enum ScanType
{
    /// <summary>The parcel arrives at the warehouse.</summary>
    Arrival = 1,

    /// <summary>The parcel leaves the warehouse for the next one.</summary>
    Dispatch = 2,

    /// <summary>The parcel is handed to a rider for delivery.</summary>
    OutForDelivery = 3,
}

/// <summary>Whether a scan recorded something new or repeated something already recorded.</summary>
public enum ScanOutcome
{
    Applied = 1,
    Duplicate = 2,
}

/// <summary>
/// One arrival or departure of a parcel at a warehouse: the parcel's movement history. Rows are only ever added, never
/// edited or deleted. Departures may name the warehouse the parcel is travelling to.
/// </summary>
public sealed class ParcelMovement : Entity
{
    private ParcelMovement()
    {
    }

    public Guid ParcelId { get; private init; }

    /// <summary>The warehouse the parcel arrived at or left from.</summary>
    public Guid WarehouseId { get; private init; }

    public ScanType ScanType { get; private init; }

    /// <summary>For a dispatch: the warehouse the parcel is travelling to, when known.</summary>
    public Guid? ToWarehouseId { get; private init; }

    public Guid StaffUserId { get; private init; }

    /// <summary>The staff member's name at the time, so the history stays readable if the account changes.</summary>
    public string? StaffName { get; private init; }

    public DateTimeOffset OccurredAtUtc { get; private init; }

    public string? Remarks { get; private init; }

    /// <summary>An arrival at a warehouse other than the one the parcel was dispatched to.</summary>
    public bool IsUnexpectedArrival { get; private init; }

    public static ParcelMovement Create(
        Guid parcelId,
        Guid warehouseId,
        ScanType scanType,
        Guid staffUserId,
        string? staffName,
        DateTimeOffset occurredAtUtc,
        string? remarks = null,
        Guid? toWarehouseId = null,
        bool isUnexpectedArrival = false)
    {
        if (!Enum.IsDefined(scanType))
        {
            throw new DomainException($"'{scanType}' is not a valid scan type.");
        }

        if (toWarehouseId is not null && scanType != ScanType.Dispatch)
        {
            throw new DomainException("Only a dispatch can name a destination warehouse.");
        }

        if (toWarehouseId == warehouseId)
        {
            throw new DomainException("A parcel cannot be dispatched to the warehouse it is leaving.");
        }

        if (isUnexpectedArrival && scanType != ScanType.Arrival)
        {
            throw new DomainException("Only an arrival can be unexpected.");
        }

        return new ParcelMovement
        {
            ParcelId = Guard.NotEmpty(parcelId, "Parcel"),
            WarehouseId = Guard.NotEmpty(warehouseId, "Warehouse"),
            ScanType = scanType,
            ToWarehouseId = toWarehouseId,
            StaffUserId = Guard.NotEmpty(staffUserId, "Staff"),
            StaffName = Guard.Optional(staffName, "Staff name", 150),
            OccurredAtUtc = occurredAtUtc,
            Remarks = Guard.Optional(remarks, "Remarks", 500),
            IsUnexpectedArrival = isUnexpectedArrival,
        };
    }
}

/// <summary>
/// The scan log: every scan that reached a known parcel at a real warehouse, including repeats. A repeat points at the movement
/// it repeated and creates no movement of its own. Rows are only ever added.
/// </summary>
public sealed class ParcelScan : Entity
{
    private ParcelScan()
    {
    }

    public Guid ParcelId { get; private init; }

    public Guid WarehouseId { get; private init; }

    public Guid StaffUserId { get; private init; }

    public string? StaffName { get; private init; }

    public ScanType ScanType { get; private init; }

    public DateTimeOffset ScannedAtUtc { get; private init; }

    public string? Remarks { get; private init; }

    public ScanOutcome Outcome { get; private init; }

    /// <summary>The movement this scan created (Applied) or repeated (Duplicate).</summary>
    public Guid MovementId { get; private init; }

    public static ParcelScan Create(
        Guid parcelId,
        Guid warehouseId,
        Guid staffUserId,
        string? staffName,
        ScanType scanType,
        DateTimeOffset scannedAtUtc,
        ScanOutcome outcome,
        Guid movementId,
        string? remarks = null)
    {
        if (!Enum.IsDefined(scanType))
        {
            throw new DomainException($"'{scanType}' is not a valid scan type.");
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new DomainException($"'{outcome}' is not a valid scan outcome.");
        }

        return new ParcelScan
        {
            ParcelId = Guard.NotEmpty(parcelId, "Parcel"),
            WarehouseId = Guard.NotEmpty(warehouseId, "Warehouse"),
            StaffUserId = Guard.NotEmpty(staffUserId, "Staff"),
            StaffName = Guard.Optional(staffName, "Staff name", 150),
            ScanType = scanType,
            ScannedAtUtc = scannedAtUtc,
            Outcome = outcome,
            MovementId = Guard.NotEmpty(movementId, "Movement"),
            Remarks = Guard.Optional(remarks, "Remarks", 500),
        };
    }
}
