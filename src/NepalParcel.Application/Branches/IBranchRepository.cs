using NepalParcel.Domain.Branches;

namespace NepalParcel.Application.Branches;

/// <summary>Persistence abstraction for <see cref="Branch"/> aggregates.</summary>
public interface IBranchRepository
{
    Task<Branch?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Branch> Items, int TotalCount)> GetPageAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    void Add(Branch branch);
}
