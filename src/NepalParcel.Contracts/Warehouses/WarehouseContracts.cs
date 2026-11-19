using NepalParcel.Contracts.Common;

namespace NepalParcel.Contracts.Warehouses;

/// <summary>Registers a warehouse or sorting hub run by a branch.</summary>
/// <param name="BranchId">The branch that operates the warehouse.</param>
/// <param name="Code">Unique alphanumeric code (max 10), e.g. KTMW1.</param>
/// <param name="Name">Display name.</param>
/// <param name="Address">Where the warehouse is.</param>
/// <param name="PhoneNumber">Nepali mobile or landline number.</param>
/// <param name="CapacityParcels">Optional capacity in parcels.</param>
public sealed record CreateWarehouseRequest(
    Guid BranchId,
    string Code,
    string Name,
    AddressDto Address,
    string PhoneNumber,
    int? CapacityParcels = null);

/// <summary>A warehouse as returned by the API.</summary>
public sealed record WarehouseResponse(
    Guid Id,
    Guid BranchId,
    string Code,
    string Name,
    AddressDto Address,
    string PhoneNumber,
    int? CapacityParcels,
    bool IsActive,
    string BranchName = "",
    int ParcelsHeld = 0,
    int ParcelsInbound = 0);

/// <summary>Changes a warehouse's details. The code and the operating branch cannot change.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Address">Where the warehouse is.</param>
/// <param name="PhoneNumber">Nepali mobile or landline number.</param>
/// <param name="CapacityParcels">Optional capacity in parcels.</param>
public sealed record UpdateWarehouseRequest(string Name, AddressDto Address, string PhoneNumber, int? CapacityParcels = null);

/// <summary>Filters for the warehouse list.</summary>
public sealed record WarehouseQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageQuery.DefaultPageSize;

    /// <summary>Part of the code, name or district.</summary>
    public string? Search { get; init; }

    public Guid? BranchId { get; init; }

    public bool IncludeInactive { get; init; }
}

/// <summary>
/// A parcel sitting in (or on its way to) a warehouse. <c>SinceUtc</c> is when it arrived (held) or was dispatched (inbound);
/// <c>WaitingHours</c> is the time since then.
/// </summary>
public sealed record InventoryParcelDto(
    Guid ParcelId,
    string TrackingNumber,
    string Status,
    string StatusName,
    string ServiceType,
    decimal WeightKg,
    int Quantity,
    string DestinationDistrict,
    DateTimeOffset SinceUtc,
    decimal WaitingHours);

/// <summary>What a warehouse holds now and what is on its way to it.</summary>
public sealed record WarehouseInventoryResponse(
    Guid WarehouseId,
    string WarehouseName,
    int? CapacityParcels,
    int ParcelsHeld,
    int ParcelsInbound,
    decimal? UtilisationPercent,
    PagedResponse<InventoryParcelDto> Held,
    IReadOnlyList<InventoryParcelDto> Inbound);

/// <summary>
/// A parcel waiting for somebody to act on it. <c>Kind</c> is Pickup (booked, waiting to be collected or handed over) or
/// Dispatch (picked up or at a warehouse, waiting to be sent on). <c>Location</c> is the branch or warehouse where it waits.
/// </summary>
public sealed record WaitingParcelDto(
    Guid ParcelId,
    string TrackingNumber,
    string Kind,
    string Status,
    string StatusName,
    string Location,
    string ServiceType,
    decimal WeightKg,
    int Quantity,
    string DestinationDistrict,
    DateTimeOffset WaitingSinceUtc,
    decimal WaitingHours);

/// <summary>Filters for parcels waiting for pickup or dispatch.</summary>
public sealed record WaitingQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageQuery.DefaultPageSize;

    /// <summary>Pickup or Dispatch; leave empty for both.</summary>
    public string? Kind { get; init; }

    public Guid? BranchId { get; init; }

    public Guid? WarehouseId { get; init; }
}
