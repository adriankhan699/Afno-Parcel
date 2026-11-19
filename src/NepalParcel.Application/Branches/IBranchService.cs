using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;

namespace NepalParcel.Application.Branches;

/// <summary>Use cases for managing the branch / warehouse network.</summary>
public interface IBranchService
{
    Task<PagedResponse<BranchResponse>> GetPageAsync(BranchQuery query, CancellationToken cancellationToken = default);

    Task<BranchResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<BranchResponse> CreateAsync(CreateBranchRequest request, CancellationToken cancellationToken = default);

    Task<BranchResponse> UpdateAsync(Guid id, UpdateBranchRequest request, CancellationToken cancellationToken = default);

    Task<BranchResponse> ActivateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Refused while the branch still operates active warehouses, has open parcels, or has staff assigned.</summary>
    Task<BranchResponse> DeactivateAsync(Guid id, CancellationToken cancellationToken = default);
}
