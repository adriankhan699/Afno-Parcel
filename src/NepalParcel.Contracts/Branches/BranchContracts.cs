using NepalParcel.Contracts.Common;

namespace NepalParcel.Contracts.Branches;

/// <summary>Request to register a new branch, head office or warehouse.</summary>
/// <param name="Code">Unique alphanumeric code (max 10), e.g. KTM01.</param>
/// <param name="Name">Display name.</param>
/// <param name="Type">HeadOffice, Branch or Warehouse.</param>
/// <param name="Address">Location of the branch.</param>
/// <param name="PhoneNumber">Nepali mobile or landline number.</param>
/// <param name="Email">Contact email (optional).</param>
public sealed record CreateBranchRequest(
    string Code,
    string Name,
    string Type,
    AddressDto Address,
    string PhoneNumber,
    string? Email = null);

/// <summary>A branch as returned by the API.</summary>
public sealed record BranchResponse(
    Guid Id,
    string Code,
    string Name,
    string Type,
    AddressDto Address,
    string PhoneNumber,
    string? Email,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);
