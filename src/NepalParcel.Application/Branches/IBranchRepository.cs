using NepalParcel.Domain.Branches;

namespace NepalParcel.Application.Branches;

/// <summary>Criteria for the branch list.</summary>
public sealed record BranchFilter(string? Search = null, BranchType? Type = null, bool IncludeInactive = false);

/// <summary>Persistence abstraction for <see cref="Branch"/> aggregates.</summary>
public interface IBranchRepository
{
    Task<Branch?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, Branch>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Branch> Items, int TotalCount)> SearchAsync(
        BranchFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    void Add(Branch branch);
}
