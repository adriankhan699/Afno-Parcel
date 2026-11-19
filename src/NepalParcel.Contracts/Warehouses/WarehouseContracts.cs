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
    bool IsActive);
