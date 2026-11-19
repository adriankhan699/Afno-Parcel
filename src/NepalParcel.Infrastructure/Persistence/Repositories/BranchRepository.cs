using Microsoft.EntityFrameworkCore;
using NepalParcel.Application.Branches;
using NepalParcel.Domain.Branches;

namespace NepalParcel.Infrastructure.Persistence.Repositories;

internal sealed class BranchRepository(NepalParcelDbContext dbContext) : IBranchRepository
{
    public Task<Branch?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Branches.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Branch>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        await dbContext.Branches
            .AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken)
            .ConfigureAwait(false);

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default) =>
        dbContext.Branches.AnyAsync(b => b.Code == code, cancellationToken);

    public async Task<(IReadOnlyList<Branch> Items, int TotalCount)> SearchAsync(
        BranchFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = dbContext.Branches.AsNoTracking();
        if (!filter.IncludeInactive)
        {
            query = query.Where(b => b.IsActive);
        }

        if (filter.Type is { } type)
        {
            query = query.Where(b => b.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var like = SqlLike.Contains(filter.Search.Trim());
            query = query.Where(b =>
                EF.Functions.Like(b.Code, like, "\\") || EF.Functions.Like(b.Name, like, "\\") || EF.Functions.Like(b.Address.District, like, "\\"));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderBy(b => b.Name)
            .ThenBy(b => b.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, total);
    }

    public void Add(Branch branch) => dbContext.Branches.Add(branch);
}
