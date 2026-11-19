using Microsoft.EntityFrameworkCore;
using NepalParcel.Application.Network;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Warehouses;

namespace NepalParcel.Infrastructure.Persistence.Repositories;

/// <summary>Helpers for case-insensitive "contains" searches that treat % and _ as ordinary characters.</summary>
internal static class SqlLike
{
    public static string Contains(string value) =>
        "%" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal) + "%";
}

internal sealed class StaffAssignmentRepository(NepalParcelDbContext dbContext) : IStaffAssignmentRepository
{
    public void Add(StaffAssignment assignment) => dbContext.StaffAssignments.Add(assignment);

    public Task<StaffAssignment?> GetActiveByUserIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        dbContext.StaffAssignments.FirstOrDefaultAsync(a => a.UserId == userId && a.EndedAtUtc == null, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, StaffAssignment>> GetActiveByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) =>
        await dbContext.StaffAssignments
            .AsNoTracking()
            .Where(a => a.EndedAtUtc == null && userIds.Contains(a.UserId))
            .ToDictionaryAsync(a => a.UserId, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Guid>> GetActiveUserIdsAsync(Guid? branchId, Guid? warehouseId, CancellationToken cancellationToken = default) =>
        await dbContext.StaffAssignments
            .AsNoTracking()
            .Where(a => a.EndedAtUtc == null
                        && (branchId == null || a.BranchId == branchId)
                        && (warehouseId == null || a.WarehouseId == warehouseId))
            .Select(a => a.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<Guid, int>> CountActiveByBranchAsync(IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken = default) =>
        await dbContext.StaffAssignments
            .AsNoTracking()
            .Where(a => a.EndedAtUtc == null && branchIds.Contains(a.BranchId))
            .GroupBy(a => a.BranchId)
            .Select(g => new { BranchId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.BranchId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);
}

internal sealed class MovementRepository(NepalParcelDbContext dbContext) : IMovementRepository
{
    public void AddMovement(ParcelMovement movement) => dbContext.ParcelMovements.Add(movement);

    public void AddScan(ParcelScan scan) => dbContext.ParcelScans.Add(scan);

    public Task<ParcelMovement?> GetLatestMovementAsync(Guid parcelId, CancellationToken cancellationToken = default) =>
        dbContext.ParcelMovements
            .AsNoTracking()
            .Where(m => m.ParcelId == parcelId)
            .OrderByDescending(m => m.OccurredAtUtc)
            .ThenByDescending(m => m.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ParcelMovement>> GetMovementsAsync(Guid parcelId, CancellationToken cancellationToken = default) =>
        await dbContext.ParcelMovements
            .AsNoTracking()
            .Where(m => m.ParcelId == parcelId)
            .OrderBy(m => m.OccurredAtUtc)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<(IReadOnlyList<ScanLogItem> Items, int TotalCount)> SearchScansAsync(
        ScanLogFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query =
            from s in dbContext.ParcelScans.AsNoTracking()
            join p in dbContext.Parcels.AsNoTracking() on s.ParcelId equals p.Id
            join w in dbContext.Warehouses.AsNoTracking() on s.WarehouseId equals w.Id
            select new { Scan = s, p.TrackingNumber, WarehouseName = w.Name };

        if (filter.WarehouseId is { } warehouseId)
        {
            query = query.Where(x => x.Scan.WarehouseId == warehouseId);
        }

        if (!string.IsNullOrWhiteSpace(filter.TrackingNumber))
        {
            var tracking = filter.TrackingNumber;
            query = query.Where(x => x.TrackingNumber.StartsWith(tracking));
        }

        if (filter.ScanType is { } scanType)
        {
            query = query.Where(x => x.Scan.ScanType == scanType);
        }

        if (filter.Outcome is { } outcome)
        {
            query = query.Where(x => x.Scan.Outcome == outcome);
        }

        if (filter.StaffUserId is { } staff)
        {
            query = query.Where(x => x.Scan.StaffUserId == staff);
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(x => x.Scan.ScannedAtUtc >= from);
        }

        if (filter.BeforeUtc is { } before)
        {
            query = query.Where(x => x.Scan.ScannedAtUtc < before);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(x => x.Scan.ScannedAtUtc)
            .ThenByDescending(x => x.Scan.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (
            rows.Select(x => new ScanLogItem(
                x.Scan.Id, x.Scan.ParcelId, x.TrackingNumber, x.Scan.WarehouseId, x.WarehouseName, x.Scan.StaffUserId, x.Scan.StaffName,
                x.Scan.ScanType, x.Scan.Outcome, x.Scan.ScannedAtUtc, x.Scan.Remarks, x.Scan.MovementId)).ToList(),
            total);
    }
}

internal sealed class InventoryRepository(NepalParcelDbContext dbContext) : IInventoryRepository
{
    private static readonly int Created = (int)ParcelStatusCode.BookingCreated;
    private static readonly int PickedUp = (int)ParcelStatusCode.PickedUp;
    private static readonly int Shipped = (int)ParcelStatusCode.Shipped;
    private static readonly int Arrived = (int)ParcelStatusCode.ArrivedAtWarehouse;
    private static readonly int InTransit = (int)ParcelStatusCode.InTransit;

    public async Task<IReadOnlyDictionary<Guid, int>> CountHeldByWarehouseAsync(IReadOnlyCollection<Guid> warehouseIds, CancellationToken cancellationToken = default) =>
        await dbContext.Parcels
            .AsNoTracking()
            .Where(p => p.CurrentStatusId == Arrived && p.CurrentWarehouseId != null && warehouseIds.Contains(p.CurrentWarehouseId.Value))
            .GroupBy(p => p.CurrentWarehouseId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<Guid, int>> CountInboundByWarehouseAsync(IReadOnlyCollection<Guid> warehouseIds, CancellationToken cancellationToken = default) =>
        await dbContext.Parcels
            .AsNoTracking()
            .Where(p => p.CurrentStatusId == InTransit && p.NextWarehouseId != null && warehouseIds.Contains(p.NextWarehouseId.Value))
            .GroupBy(p => p.NextWarehouseId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

    public Task<int> CountOpenAtBranchAsync(Guid branchId, CancellationToken cancellationToken = default) =>
        dbContext.Parcels.CountAsync(
            p => p.CurrentBranchId == branchId && (p.CurrentStatusId == Created || p.CurrentStatusId == PickedUp || p.CurrentStatusId == Shipped),
            cancellationToken);

    public async Task<(IReadOnlyList<InventoryRow> Items, int TotalCount)> GetHeldAsync(
        Guid warehouseId,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Parcels.AsNoTracking().Where(p => p.CurrentWarehouseId == warehouseId && p.CurrentStatusId == Arrived);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = TrackingNumbers.TryExtract(search, out var tracking) ? tracking : TrackingNumbers.Normalize(search);
            var like = SqlLike.Contains(search.Trim());
            // Part of a tracking number (any part, as people type what they can read off a label) or the receiver's district.
            query = query.Where(p => p.TrackingNumber.Contains(term) || EF.Functions.Like(p.ReceiverAddress.District, like, "\\"));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderBy(p => p.StatusChangedAtUtc)
            .ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new InventoryRow(p.Id, p.TrackingNumber, p.CurrentStatusId, p.ServiceType, p.WeightKg, p.Quantity, p.ReceiverAddress.District, p.StatusChangedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, total);
    }

    public async Task<IReadOnlyList<InventoryRow>> GetInboundAsync(Guid warehouseId, int take, CancellationToken cancellationToken = default) =>
        await dbContext.Parcels
            .AsNoTracking()
            .Where(p => p.NextWarehouseId == warehouseId && p.CurrentStatusId == InTransit)
            .OrderBy(p => p.StatusChangedAtUtc)
            .ThenBy(p => p.Id)
            .Take(take)
            .Select(p => new InventoryRow(p.Id, p.TrackingNumber, p.CurrentStatusId, p.ServiceType, p.WeightKg, p.Quantity, p.ReceiverAddress.District, p.StatusChangedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<(IReadOnlyList<WaitingRow> Items, int TotalCount)> GetWaitingAsync(
        WaitingFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query =
            from p in dbContext.Parcels.AsNoTracking()
            join b in dbContext.Branches.AsNoTracking() on p.OriginBranchId equals b.Id
            join w0 in dbContext.Warehouses.AsNoTracking() on p.CurrentWarehouseId equals w0.Id into warehouses
            from w in warehouses.DefaultIfEmpty()
            select new { Parcel = p, BranchName = b.Name, WarehouseName = w.Name, WarehouseBranchId = (Guid?)w.BranchId };

        query = filter.Kind switch
        {
            WaitingKind.Pickup => query.Where(x => x.Parcel.CurrentStatusId == Created),
            WaitingKind.Dispatch => query.Where(x => x.Parcel.CurrentStatusId == PickedUp || x.Parcel.CurrentStatusId == Arrived),
            _ => query.Where(x => x.Parcel.CurrentStatusId == Created || x.Parcel.CurrentStatusId == PickedUp || x.Parcel.CurrentStatusId == Arrived),
        };

        if (filter.WarehouseId is { } warehouseId)
        {
            query = query.Where(x => x.Parcel.CurrentStatusId == Arrived && x.Parcel.CurrentWarehouseId == warehouseId);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(x =>
                ((x.Parcel.CurrentStatusId == Created || x.Parcel.CurrentStatusId == PickedUp) && x.Parcel.OriginBranchId == branchId)
                || (x.Parcel.CurrentStatusId == Arrived && x.WarehouseBranchId == branchId));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderBy(x => x.Parcel.StatusChangedAtUtc)
            .ThenBy(x => x.Parcel.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Parcel.Id,
                x.Parcel.TrackingNumber,
                x.Parcel.CurrentStatusId,
                x.BranchName,
                x.WarehouseName,
                x.Parcel.ServiceType,
                x.Parcel.WeightKg,
                x.Parcel.Quantity,
                District = x.Parcel.ReceiverAddress.District,
                Since = x.Parcel.StatusChangedAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (
            rows.Select(r => new WaitingRow(
                r.Id,
                r.TrackingNumber,
                r.CurrentStatusId,
                r.CurrentStatusId == Arrived ? r.WarehouseName ?? r.BranchName : r.BranchName,
                r.ServiceType,
                r.WeightKg,
                r.Quantity,
                r.District,
                r.Since)).ToList(),
            total);
    }
}
