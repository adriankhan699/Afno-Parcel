using Microsoft.EntityFrameworkCore;
using NepalParcel.Application.Tracking;
using NepalParcel.Domain.Notifications;
using NepalParcel.Domain.Tracking;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.Infrastructure.Persistence.Repositories;

internal sealed class WarehouseRepository(NepalParcelDbContext dbContext) : IWarehouseRepository
{
    public Task<Warehouse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Warehouses.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Warehouse>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        await dbContext.Warehouses
            .AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, cancellationToken)
            .ConfigureAwait(false);

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default) =>
        dbContext.Warehouses.AnyAsync(w => w.Code == code, cancellationToken);

    public async Task<(IReadOnlyList<Warehouse> Items, int TotalCount)> SearchAsync(WarehouseFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = dbContext.Warehouses.AsNoTracking();
        if (!filter.IncludeInactive)
        {
            query = query.Where(w => w.IsActive);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(w => w.BranchId == branchId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var like = SqlLike.Contains(filter.Search.Trim());
            query = query.Where(w =>
                EF.Functions.Like(w.Code, like, "\\") || EF.Functions.Like(w.Name, like, "\\") || EF.Functions.Like(w.Address.District, like, "\\"));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderBy(w => w.Name)
            .ThenBy(w => w.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, total);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountByBranchAsync(IReadOnlyCollection<Guid> branchIds, bool activeOnly, CancellationToken cancellationToken = default) =>
        await dbContext.Warehouses
            .AsNoTracking()
            .Where(w => branchIds.Contains(w.BranchId) && (!activeOnly || w.IsActive))
            .GroupBy(w => w.BranchId)
            .Select(g => new { BranchId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.BranchId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

    public void Add(Warehouse warehouse) => dbContext.Warehouses.Add(warehouse);
}

internal sealed class TrackingOtpRepository(NepalParcelDbContext dbContext) : ITrackingOtpRepository
{
    public void Add(TrackingOtp otp) => dbContext.TrackingOtps.Add(otp);

    public Task<TrackingOtp?> GetLatestAsync(Guid parcelId, CancellationToken cancellationToken = default) =>
        dbContext.TrackingOtps
            .Where(o => o.ParcelId == parcelId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<int> CountSinceAsync(Guid parcelId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default) =>
        dbContext.TrackingOtps.CountAsync(o => o.ParcelId == parcelId && o.CreatedAtUtc >= sinceUtc, cancellationToken);
}
