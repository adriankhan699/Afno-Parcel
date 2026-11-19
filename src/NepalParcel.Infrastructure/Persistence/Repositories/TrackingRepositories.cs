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

    public async Task<(IReadOnlyList<Warehouse> Items, int TotalCount)> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Warehouses.AsNoTracking();
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

internal sealed class NotificationOutboxRepository(NepalParcelDbContext dbContext) : INotificationOutboxRepository
{
    public void Add(NotificationOutboxMessage message) => dbContext.NotificationOutbox.Add(message);
}
