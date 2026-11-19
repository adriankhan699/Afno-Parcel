using Microsoft.EntityFrameworkCore;
using NepalParcel.Application.Payments;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Infrastructure.Persistence.Repositories;

internal sealed class PaymentTransactionRepository(NepalParcelDbContext dbContext) : IPaymentTransactionRepository
{
    public void Add(PaymentTransaction transaction) => dbContext.PaymentTransactions.Add(transaction);

    public Task<PaymentTransaction?> GetByMerchantReferenceAsync(string merchantReference, CancellationToken cancellationToken = default) =>
        dbContext.PaymentTransactions.FirstOrDefaultAsync(t => t.MerchantReference == merchantReference, cancellationToken);

    public async Task<IReadOnlyList<PaymentTransaction>> GetPendingForPaymentAsync(Guid paymentId, CancellationToken cancellationToken = default) =>
        await dbContext.PaymentTransactions
            .Where(t => t.PaymentId == paymentId && t.Status == TransactionStatus.Pending)
            .OrderBy(t => t.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<bool> ProviderReferenceUsedAsync(string provider, string providerReference, Guid exceptTransactionId, CancellationToken cancellationToken = default) =>
        dbContext.PaymentTransactions.AnyAsync(
            t => t.Provider == provider && t.ProviderReference == providerReference && t.Id != exceptTransactionId,
            cancellationToken);

    public async Task<IReadOnlyList<PaymentTransaction>> GetByPaymentsAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken cancellationToken = default) =>
        await dbContext.PaymentTransactions
            .AsNoTracking()
            .Where(t => paymentIds.Contains(t.PaymentId))
            .OrderBy(t => t.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<(IReadOnlyList<TransactionListItem> Items, int TotalCount)> SearchAsync(TransactionFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query =
            from t in dbContext.PaymentTransactions.AsNoTracking()
            join p in dbContext.Payments.AsNoTracking() on t.PaymentId equals p.Id
            join parcel in dbContext.Parcels.AsNoTracking() on p.ParcelId equals parcel.Id
            select new { Transaction = t, ParcelId = parcel.Id, parcel.TrackingNumber, parcel.OriginBranchId };

        if (filter.Provider is { } provider)
        {
            query = query.Where(x => x.Transaction.Provider == provider);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(x => x.Transaction.Status == status);
        }

        if (filter.ParcelId is { } parcelId)
        {
            query = query.Where(x => x.ParcelId == parcelId);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(x => x.OriginBranchId == branchId);
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(x => x.Transaction.CreatedAtUtc >= from);
        }

        if (filter.BeforeUtc is { } before)
        {
            query = query.Where(x => x.Transaction.CreatedAtUtc < before);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToUpperInvariant();
            var tracking = TrackingNumbers.TryExtract(filter.Search, out var number) ? number : TrackingNumbers.Normalize(filter.Search);
            query = query.Where(x =>
                x.Transaction.MerchantReference.Contains(term)
                || (x.Transaction.ProviderReference != null && x.Transaction.ProviderReference.Contains(filter.Search.Trim()))
                || (tracking != string.Empty && x.TrackingNumber.Contains(tracking)));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(x => x.Transaction.CreatedAtUtc)
            .ThenByDescending(x => x.Transaction.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return (rows.Select(r => new TransactionListItem(r.Transaction, r.ParcelId, r.TrackingNumber)).ToList(), total);
    }
}

internal sealed class PaymentReportRepository(NepalParcelDbContext dbContext) : IPaymentReportRepository
{
    public async Task<IReadOnlyList<PaymentReportRow>> GetPaymentRowsAsync(DateTimeOffset fromUtc, DateTimeOffset beforeUtc, Guid? branchId, CancellationToken cancellationToken = default)
    {
        var query =
            from p in dbContext.Payments.AsNoTracking()
            join parcel in dbContext.Parcels.AsNoTracking() on p.ParcelId equals parcel.Id
            where (p.PaidAtUtc >= fromUtc && p.PaidAtUtc < beforeUtc) || (p.CreatedAtUtc >= fromUtc && p.CreatedAtUtc < beforeUtc)
            select new { Payment = p, parcel.OriginBranchId };
        if (branchId is { } branch)
        {
            query = query.Where(x => x.OriginBranchId == branch);
        }

        return await query
            .Select(x => new PaymentReportRow(x.Payment.Method, x.Payment.Status, x.Payment.Amount, x.Payment.CreatedAtUtc, x.Payment.PaidAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TransactionStatRow>> GetTransactionStatsAsync(DateTimeOffset fromUtc, DateTimeOffset beforeUtc, Guid? branchId, CancellationToken cancellationToken = default)
    {
        var query =
            from t in dbContext.PaymentTransactions.AsNoTracking()
            join p in dbContext.Payments.AsNoTracking() on t.PaymentId equals p.Id
            join parcel in dbContext.Parcels.AsNoTracking() on p.ParcelId equals parcel.Id
            where t.CreatedAtUtc >= fromUtc && t.CreatedAtUtc < beforeUtc
            select new { Transaction = t, parcel.OriginBranchId };
        if (branchId is { } branch)
        {
            query = query.Where(x => x.OriginBranchId == branch);
        }

        return await query
            .Select(x => new TransactionStatRow(x.Transaction.Provider, x.Transaction.Status, x.Transaction.Type, x.Transaction.Amount))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SettlementStatRow>> GetSettlementStatsAsync(DateTimeOffset fromUtc, DateTimeOffset beforeUtc, Guid? branchId, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CodSettlements.AsNoTracking().Where(s => s.CreatedAtUtc >= fromUtc && s.CreatedAtUtc < beforeUtc);
        if (branchId is { } branch)
        {
            query = query.Where(s => s.BranchId == branch);
        }

        return await query
            .Select(s => new SettlementStatRow(s.Status, s.TotalAmount, s.CountedAmount))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class CodSettlementRepository(NepalParcelDbContext dbContext) : ICodSettlementRepository
{
    public void Add(CodSettlement settlement) => dbContext.CodSettlements.Add(settlement);

    public Task<CodSettlement?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.CodSettlements.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<bool> ReferenceExistsAsync(string referenceNumber, CancellationToken cancellationToken = default) =>
        dbContext.CodSettlements.AnyAsync(s => s.ReferenceNumber == referenceNumber, cancellationToken);

    public async Task<(IReadOnlyList<CodSettlement> Items, int TotalCount)> SearchAsync(SettlementFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = dbContext.CodSettlements.AsNoTracking().Where(s => s.Status != SettlementStatus.Draft);
        if (filter.Status is { } status)
        {
            query = query.Where(s => s.Status == status);
        }

        if (filter.AgentId is { } agentId)
        {
            query = query.Where(s => s.DeliveryAgentId == agentId);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(s => s.BranchId == branchId);
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(s => s.CreatedAtUtc >= from);
        }

        if (filter.BeforeUtc is { } before)
        {
            query = query.Where(s => s.CreatedAtUtc < before);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(s => s.CreatedAtUtc)
            .ThenByDescending(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return (items, total);
    }
}

internal sealed class TestGatewaySessionRepository(NepalParcelDbContext dbContext) : ITestGatewaySessions
{
    public void Add(TestGatewaySession session) => dbContext.TestGatewaySessions.Add(session);

    public Task<TestGatewaySession?> GetByReferenceAsync(string merchantReference, CancellationToken cancellationToken = default) =>
        dbContext.TestGatewaySessions.FirstOrDefaultAsync(s => s.MerchantReference == merchantReference, cancellationToken);
}
