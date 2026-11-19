using NepalParcel.Application.Common.Mapping;
using NepalParcel.Contracts.Branches;
using NepalParcel.Domain.Branches;

namespace NepalParcel.Application.Branches;

internal static class BranchMappings
{
    public static BranchResponse ToResponse(this Branch branch, int warehouseCount = 0, int staffCount = 0) =>
        new(
            branch.Id,
            branch.Code,
            branch.Name,
            branch.Type.ToString(),
            branch.Address.ToDto(),
            branch.PhoneNumber,
            branch.Email,
            branch.IsActive,
            branch.CreatedAtUtc,
            branch.UpdatedAtUtc,
            warehouseCount,
            staffCount);
}
