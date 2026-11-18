using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;

namespace NepalParcel.Application.Branches;

/// <summary>Use cases for managing the branch / warehouse network.</summary>
public interface IBranchService
{
    Task<PagedResponse<BranchResponse>> GetPageAsync(PageQuery query, CancellationToken cancellationToken = default);

    Task<BranchResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<BranchResponse> CreateAsync(CreateBranchRequest request, CancellationToken cancellationToken = default);
}
