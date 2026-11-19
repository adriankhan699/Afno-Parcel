using Microsoft.EntityFrameworkCore;
using NepalParcel.Application.Delivery;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Infrastructure.Persistence.Repositories;

internal sealed class DeliveryAgentRepository(NepalParcelDbContext dbContext) : IDeliveryAgentRepository
{
    public void Add(DeliveryAgent agent) => dbContext.DeliveryAgents.Add(agent);

    public Task<DeliveryAgent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.DeliveryAgents.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<DeliveryAgent?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
        dbContext.DeliveryAgents.FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, DeliveryAgent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        await dbContext.DeliveryAgents
            .AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, cancellationToken)
            .ConfigureAwait(false);

    public async Task<(IReadOnlyList<DeliveryAgent> Items, int TotalCount)> SearchAsync(AgentFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = dbContext.DeliveryAgents.AsNoTracking();
        if (filter.BranchId is { } branchId)
        {
            query = query.Where(a => a.BranchId == branchId);
        }

        if (filter.IsActive is { } active)
        {
            query = query.Where(a => a.IsActive == active);
        }

        if (filter.IsAvailable is { } available)
        {
            query = query.Where(a => a.IsAvailable == available);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var like = SqlLike.Contains(filter.Search.Trim());
            query = query.Where(a =>
                EF.Functions.Like(a.FullName, like, "\\") ||
                EF.Functions.Like(a.PhoneNumber, like, "\\") ||
                (a.VehicleNumber != null && EF.Functions.Like(a.VehicleNumber, like, "\\")));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderBy(a => a.FullName)
            .ThenBy(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return (items, total);
    }

    public async Task<IReadOnlyList<DeliveryAgent>> GetForReportAsync(Guid? branchId, Guid? agentId, CancellationToken cancellationToken = default)
    {
        var query = dbContext.DeliveryAgents.AsNoTracking();
        if (branchId is { } branch)
        {
            query = query.Where(a => a.BranchId == branch);
        }

        if (agentId is { } agent)
        {
            query = query.Where(a => a.Id == agent);
        }

        return await query.OrderBy(a => a.FullName).ThenBy(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountOpenAssignmentsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken cancellationToken = default) =>
        await dbContext.DeliveryAssignments
            .AsNoTracking()
            .Where(a => agentIds.Contains(a.DeliveryAgentId) && a.Type == AssignmentType.Delivery
                        && (a.Status == AssignmentStatus.Assigned || a.Status == AssignmentStatus.Accepted || a.Status == AssignmentStatus.InProgress))
            .GroupBy(a => a.DeliveryAgentId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken)
            .ConfigureAwait(false);
}

internal sealed class DeliveryAssignmentRepository(NepalParcelDbContext dbContext) : IDeliveryAssignmentRepository
{
    private static readonly int FailedStatusId = (int)ParcelStatusCode.DeliveryFailed;

    private static readonly int[] ReadyStatusIds =
    [
        (int)ParcelStatusCode.Shipped,
        (int)ParcelStatusCode.InTransit,
        (int)ParcelStatusCode.ArrivedAtWarehouse,
        (int)ParcelStatusCode.DeliveryFailed,
    ];

    public void Add(DeliveryAssignment assignment) => dbContext.DeliveryAssignments.Add(assignment);

    public Task<DeliveryAssignment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.DeliveryAssignments.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<DeliveryAssignment?> GetOpenForParcelAsync(Guid parcelId, AssignmentType type, CancellationToken cancellationToken = default) =>
        dbContext.DeliveryAssignments.FirstOrDefaultAsync(
            a => a.ParcelId == parcelId && a.Type == type
                 && (a.Status == AssignmentStatus.Assigned || a.Status == AssignmentStatus.Accepted || a.Status == AssignmentStatus.InProgress),
            cancellationToken);

    public async Task<int> GetLastAttemptNumberAsync(Guid parcelId, AssignmentType type, CancellationToken cancellationToken = default)
    {
        var attempts = await dbContext.DeliveryAssignments
            .AsNoTracking()
            .Where(a => a.ParcelId == parcelId && a.Type == type)
            .Select(a => a.AttemptNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return attempts.Count == 0 ? 0 : attempts.Max();
    }

    public Task<bool> HasLaterAttemptAsync(Guid parcelId, int attemptNumber, CancellationToken cancellationToken = default) =>
        dbContext.DeliveryAssignments.AnyAsync(
            a => a.ParcelId == parcelId && a.Type == AssignmentType.Delivery && a.AttemptNumber > attemptNumber,
            cancellationToken);

    public async Task<(IReadOnlyList<DeliveryAssignment> Items, int TotalCount)> SearchAsync(AssignmentFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = dbContext.DeliveryAssignments.AsNoTracking().Where(a => a.Type == AssignmentType.Delivery);
        if (filter.AgentId is { } agentId)
        {
            query = query.Where(a => a.DeliveryAgentId == agentId);
        }

        if (filter.ParcelId is { } parcelId)
        {
            query = query.Where(a => a.ParcelId == parcelId);
        }

        if (filter.AgentBranchId is { } branchId)
        {
            query = query.Where(a => dbContext.DeliveryAgents.Any(g => g.Id == a.DeliveryAgentId && g.BranchId == branchId));
        }

        if (filter.Statuses is { Count: > 0 } statuses)
        {
            var list = statuses.ToList();
            query = query.Where(a => list.Contains(a.Status));
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(a => a.AssignedAtUtc >= from);
        }

        if (filter.BeforeUtc is { } before)
        {
            query = query.Where(a => a.AssignedAtUtc < before);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(a => a.AssignedAtUtc)
            .ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return (items, total);
    }

    public async Task<(IReadOnlyList<DeliveryAssignment> Items, int TotalCount)> GetForAgentAsync(Guid agentId, bool open, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var rows = from a in dbContext.DeliveryAssignments.AsNoTracking()
                   join p in dbContext.Parcels.AsNoTracking() on a.ParcelId equals p.Id
                   where a.DeliveryAgentId == agentId && a.Type == AssignmentType.Delivery
                   select new { Assignment = a, ParcelStatusId = p.CurrentStatusId };

        // Still to do, or failed and waiting for the parcel to be taken back to a warehouse (and not given to anyone since).
        var query = open
            ? rows.Where(x =>
                x.Assignment.Status == AssignmentStatus.Assigned || x.Assignment.Status == AssignmentStatus.Accepted || x.Assignment.Status == AssignmentStatus.InProgress
                || (x.Assignment.Status == AssignmentStatus.Failed && x.ParcelStatusId == FailedStatusId
                    && !dbContext.DeliveryAssignments.Any(o => o.ParcelId == x.Assignment.ParcelId && o.Type == AssignmentType.Delivery && o.AttemptNumber > x.Assignment.AttemptNumber)))
            : rows.Where(x =>
                !(x.Assignment.Status == AssignmentStatus.Assigned || x.Assignment.Status == AssignmentStatus.Accepted || x.Assignment.Status == AssignmentStatus.InProgress
                  || (x.Assignment.Status == AssignmentStatus.Failed && x.ParcelStatusId == FailedStatusId
                      && !dbContext.DeliveryAssignments.Any(o => o.ParcelId == x.Assignment.ParcelId && o.Type == AssignmentType.Delivery && o.AttemptNumber > x.Assignment.AttemptNumber))));

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var ordered = open
            ? query.OrderBy(x => x.Assignment.AssignedAtUtc).ThenBy(x => x.Assignment.Id)
            : query.OrderByDescending(x => x.Assignment.AssignedAtUtc).ThenByDescending(x => x.Assignment.Id);
        var items = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.Assignment)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return (items, total);
    }

    public async Task<IReadOnlyList<AssignmentStat>> GetStatsAsync(DateTimeOffset fromUtc, DateTimeOffset beforeUtc, Guid? branchId, Guid? agentId, CancellationToken cancellationToken = default)
    {
        var query = dbContext.DeliveryAssignments
            .AsNoTracking()
            .Where(a => a.Type == AssignmentType.Delivery && a.AssignedAtUtc >= fromUtc && a.AssignedAtUtc < beforeUtc);
        if (agentId is { } agent)
        {
            query = query.Where(a => a.DeliveryAgentId == agent);
        }

        if (branchId is { } branch)
        {
            query = query.Where(a => dbContext.DeliveryAgents.Any(g => g.Id == a.DeliveryAgentId && g.BranchId == branch));
        }

        return await query
            .Select(a => new AssignmentStat(a.DeliveryAgentId, a.Status, a.AttemptNumber, a.AssignedAtUtc, a.CompletedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(int Delivered, int Failed)> CountFinishedSinceAsync(Guid agentId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        var delivered = await dbContext.DeliveryAssignments.CountAsync(
            a => a.DeliveryAgentId == agentId && a.Type == AssignmentType.Delivery && a.Status == AssignmentStatus.Completed && a.CompletedAtUtc >= sinceUtc,
            cancellationToken).ConfigureAwait(false);
        var failed = await dbContext.DeliveryAssignments.CountAsync(
            a => a.DeliveryAgentId == agentId && a.Type == AssignmentType.Delivery && a.Status == AssignmentStatus.Failed && a.CompletedAtUtc >= sinceUtc,
            cancellationToken).ConfigureAwait(false);
        return (delivered, failed);
    }

    public async Task<(IReadOnlyList<ReadyParcelRow> Items, int TotalCount)> GetReadyParcelsAsync(ReadyFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query =
            from p in dbContext.Parcels.AsNoTracking()
            join w0 in dbContext.Warehouses.AsNoTracking() on p.CurrentWarehouseId equals w0.Id into warehouses
            from w in warehouses.DefaultIfEmpty()
            join b0 in dbContext.Branches.AsNoTracking() on p.CurrentBranchId equals b0.Id into branches
            from b in branches.DefaultIfEmpty()
            where ReadyStatusIds.Contains(p.CurrentStatusId)
                  && !dbContext.DeliveryAssignments.Any(a => a.ParcelId == p.Id && a.Type == AssignmentType.Delivery
                      && (a.Status == AssignmentStatus.Assigned || a.Status == AssignmentStatus.Accepted || a.Status == AssignmentStatus.InProgress))
            select new { Parcel = p, WarehouseName = w.Name, WarehouseBranchId = (Guid?)w.BranchId, BranchName = b.Name };

        if (filter.BranchId is { } branchId)
        {
            // A parcel whose delivery failed is on the road with no place recorded: it belongs to the branch of the agent who tried.
            query = query.Where(x =>
                x.Parcel.CurrentBranchId == branchId
                || x.WarehouseBranchId == branchId
                || x.Parcel.DestinationBranchId == branchId
                || (x.Parcel.CurrentStatusId == FailedStatusId
                    && dbContext.DeliveryAssignments.Any(a => a.ParcelId == x.Parcel.Id && a.Type == AssignmentType.Delivery
                        && dbContext.DeliveryAgents.Any(g => g.Id == a.DeliveryAgentId && g.BranchId == branchId))));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = TrackingNumbers.TryExtract(filter.Search, out var tracking) ? tracking : TrackingNumbers.Normalize(filter.Search);
            var like = SqlLike.Contains(filter.Search.Trim());
            query = query.Where(x =>
                (term != string.Empty && x.Parcel.TrackingNumber.Contains(term)) ||
                EF.Functions.Like(x.Parcel.ReceiverName, like, "\\") ||
                EF.Functions.Like(x.Parcel.ReceiverAddress.District, like, "\\"));
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
                x.WarehouseName,
                x.BranchName,
                x.Parcel.ReceiverName,
                District = x.Parcel.ReceiverAddress.District,
                Municipality = x.Parcel.ReceiverAddress.Municipality,
                x.Parcel.ServiceType,
                x.Parcel.CodAmount,
                Failed = dbContext.DeliveryAssignments.Count(a => a.ParcelId == x.Parcel.Id && a.Type == AssignmentType.Delivery && a.Status == AssignmentStatus.Failed),
                Since = x.Parcel.StatusChangedAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (
            rows.Select(r => new ReadyParcelRow(
                r.Id,
                r.TrackingNumber,
                r.CurrentStatusId,
                r.WarehouseName ?? r.BranchName ?? (r.CurrentStatusId == FailedStatusId ? "With the delivery agent" : "On the way"),
                r.ReceiverName,
                r.District,
                r.Municipality,
                r.ServiceType,
                r.CodAmount,
                r.Failed,
                r.Since)).ToList(),
            total);
    }
}

internal sealed class DeliveryProofRepository(NepalParcelDbContext dbContext) : IDeliveryProofRepository
{
    public void Add(DeliveryProof proof) => dbContext.DeliveryProofs.Add(proof);

    public Task<DeliveryProof?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.DeliveryProofs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<DeliveryProof>> GetByAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default) =>
        await dbContext.DeliveryProofs
            .AsNoTracking()
            .Where(p => p.AssignmentId == assignmentId)
            .OrderBy(p => p.CapturedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DeliveryProof>>> GetByAssignmentsAsync(IReadOnlyCollection<Guid> assignmentIds, CancellationToken cancellationToken = default)
    {
        var proofs = await dbContext.DeliveryProofs
            .AsNoTracking()
            .Where(p => p.AssignmentId != null && assignmentIds.Contains(p.AssignmentId.Value))
            .OrderBy(p => p.CapturedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return proofs
            .GroupBy(p => p.AssignmentId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DeliveryProof>)g.ToList());
    }

    public Task<int> CountForAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default) =>
        dbContext.DeliveryProofs.CountAsync(p => p.AssignmentId == assignmentId, cancellationToken);
}

internal sealed class CodCollectionRepository(NepalParcelDbContext dbContext) : ICodCollectionRepository
{
    public void Add(CodCollection collection) => dbContext.CodCollections.Add(collection);

    public Task<CodCollection?> GetByParcelAsync(Guid parcelId, CancellationToken cancellationToken = default) =>
        dbContext.CodCollections.AsNoTracking().FirstOrDefaultAsync(c => c.ParcelId == parcelId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, CodCollection>> GetByParcelsAsync(IReadOnlyCollection<Guid> parcelIds, CancellationToken cancellationToken = default) =>
        await dbContext.CodCollections
            .AsNoTracking()
            .Where(c => parcelIds.Contains(c.ParcelId))
            .ToDictionaryAsync(c => c.ParcelId, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<CodStat>> GetStatsAsync(DateTimeOffset fromUtc, DateTimeOffset beforeUtc, Guid? branchId, Guid? agentId, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CodCollections.AsNoTracking().Where(c => c.CollectedAtUtc >= fromUtc && c.CollectedAtUtc < beforeUtc);
        if (agentId is { } agent)
        {
            query = query.Where(c => c.DeliveryAgentId == agent);
        }

        if (branchId is { } branch)
        {
            query = query.Where(c => dbContext.DeliveryAgents.Any(g => g.Id == c.DeliveryAgentId && g.BranchId == branch));
        }

        return await query
            .Select(c => new CodStat(c.DeliveryAgentId, c.CollectedAmount))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CodCollection>> GetInHandForAgentAsync(Guid agentId, CancellationToken cancellationToken = default) =>
        await dbContext.CodCollections
            .Where(c => c.DeliveryAgentId == agentId && c.SettlementId == null && c.Status == CodCollectionStatus.Collected)
            .OrderBy(c => c.CollectedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<CodCollection>> GetBySettlementAsync(Guid settlementId, CancellationToken cancellationToken = default) =>
        await dbContext.CodCollections
            .Where(c => c.SettlementId == settlementId)
            .OrderBy(c => c.CollectedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<NepalParcel.Application.Payments.CodRow>> GetCodRowsAsync(Guid? branchId, Guid? agentId, CancellationToken cancellationToken = default)
    {
        var query =
            from c in dbContext.CodCollections.AsNoTracking()
            join a in dbContext.DeliveryAgents.AsNoTracking() on c.DeliveryAgentId equals a.Id
            select new { Collection = c, a.BranchId };
        if (branchId is { } branch)
        {
            query = query.Where(x => x.BranchId == branch);
        }

        if (agentId is { } agent)
        {
            query = query.Where(x => x.Collection.DeliveryAgentId == agent);
        }

        return await query
            .Select(x => new NepalParcel.Application.Payments.CodRow(
                x.Collection.DeliveryAgentId, x.Collection.CollectedAmount, x.Collection.Status, x.Collection.SettlementId, x.Collection.CollectedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<decimal> SumUnsettledAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        // Summed in memory: SQLite (used by the tests) cannot add up decimal columns in SQL.
        var amounts = await dbContext.CodCollections
            .AsNoTracking()
            .Where(c => c.DeliveryAgentId == agentId && c.SettlementId == null && c.Status == CodCollectionStatus.Collected)
            .Select(c => c.CollectedAmount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return amounts.Sum();
    }

    public async Task<decimal> SumCollectedSinceAsync(Guid agentId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        var amounts = await dbContext.CodCollections
            .AsNoTracking()
            .Where(c => c.DeliveryAgentId == agentId && c.CollectedAtUtc >= sinceUtc)
            .Select(c => c.CollectedAmount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return amounts.Sum();
    }
}

internal sealed class DeliveryOtpRepository(NepalParcelDbContext dbContext) : IDeliveryOtpRepository
{
    public void Add(DeliveryOtp otp) => dbContext.DeliveryOtps.Add(otp);

    public Task<DeliveryOtp?> GetLatestAsync(Guid assignmentId, CancellationToken cancellationToken = default) =>
        dbContext.DeliveryOtps
            .Where(o => o.AssignmentId == assignmentId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<int> CountSinceAsync(Guid assignmentId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default) =>
        dbContext.DeliveryOtps.CountAsync(o => o.AssignmentId == assignmentId && o.CreatedAtUtc >= sinceUtc, cancellationToken);
}
