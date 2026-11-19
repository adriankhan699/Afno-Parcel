using FluentValidation;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Delivery;
using NepalParcel.Application.Network;
using NepalParcel.Contracts.Payments;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Application.Payments;

/// <summary>
/// The accountant's summary: what was paid, what is pending, what failed, the split by method and provider, a daily series, the
/// cash-on-delivery position and the settlements. A branch manager always gets their own branch; accountants and super
/// administrators can look at one branch or at everything.
/// </summary>
internal sealed class PaymentReportService(
    IPaymentReportRepository reports,
    ICodCollectionRepository cods,
    WorkplaceAccess access,
    TimeProvider clock,
    IValidator<PaymentReportQuery> validator) : IPaymentReportService
{
    public async Task<PaymentReportResponse> GetSummaryAsync(PaymentReportQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await validator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var branchId = await access.ScopeFinanceBranchAsync(query.BranchId, cancellationToken).ConfigureAwait(false);
        var to = query.To ?? NepalDay.Today(clock.GetUtcNow());
        var from = query.From ?? to.AddDays(-29);
        var fromUtc = NepalDay.StartUtc(from);
        var beforeUtc = NepalDay.StartUtc(to.AddDays(1));

        var payments = await reports.GetPaymentRowsAsync(fromUtc, beforeUtc, branchId, cancellationToken).ConfigureAwait(false);
        var paid = payments.Where(p => p.Status == PaymentStatus.Paid && p.PaidAtUtc is { } at && at >= fromUtc && at < beforeUtc).ToList();
        var pending = payments.Where(p => p.Status == PaymentStatus.Pending && p.CreatedAtUtc >= fromUtc && p.CreatedAtUtc < beforeUtc).ToList();

        var byMethod = paid
            .GroupBy(p => p.Method)
            .OrderBy(g => g.Key)
            .Select(g => new MethodTotal(g.Key.ToString(), g.Count(), g.Sum(p => p.Amount)))
            .ToList();

        var stats = await reports.GetTransactionStatsAsync(fromUtc, beforeUtc, branchId, cancellationToken).ConfigureAwait(false);
        var byProvider = stats
            .Where(s => s.Type == TransactionType.Charge)
            .GroupBy(s => s.Provider, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ProviderTotal(
                g.Key,
                g.Count(s => s.Status == TransactionStatus.Succeeded),
                g.Count(s => s.Status == TransactionStatus.Failed),
                g.Count(s => s.Status == TransactionStatus.Pending),
                g.Where(s => s.Status == TransactionStatus.Succeeded).Sum(s => s.Amount)))
            .ToList();

        var perDay = paid
            .GroupBy(p => NepalDay.Today(p.PaidAtUtc!.Value))
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Amount: g.Sum(p => p.Amount)));
        var daily = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(offset => from.AddDays(offset))
            .Select(day => perDay.TryGetValue(day, out var total) ? new DailyTotal(day, total.Count, total.Amount) : new DailyTotal(day, 0, 0m))
            .ToList();

        var cash = await cods.GetCodRowsAsync(branchId, null, cancellationToken).ConfigureAwait(false);
        var collectedInPeriod = cash.Where(r => r.CollectedAtUtc >= fromUtc && r.CollectedAtUtc < beforeUtc).ToList();
        decimal Position(Func<CodRow, bool> where) => cash.Where(where).Sum(r => r.CollectedAmount);
        var cod = new CodReport(
            collectedInPeriod.Count,
            collectedInPeriod.Sum(r => r.CollectedAmount),
            Position(r => r.Status == CodCollectionStatus.Collected && r.SettlementId is null),
            Position(r => r.Status == CodCollectionStatus.Collected && r.SettlementId is not null),
            Position(r => r.Status == CodCollectionStatus.Disputed),
            Position(r => r.Status == CodCollectionStatus.Settled));

        var settlementRows = await reports.GetSettlementStatsAsync(fromUtc, beforeUtc, branchId, cancellationToken).ConfigureAwait(false);
        var settlements = new SettlementReport(
            settlementRows.Count(s => s.Status == SettlementStatus.Submitted),
            settlementRows.Count(s => s.Status == SettlementStatus.Verified),
            settlementRows.Count(s => s.Status == SettlementStatus.Disputed),
            settlementRows.Where(s => s.CountedAmount is not null).Sum(s => s.CountedAmount!.Value - s.TotalAmount));

        return new PaymentReportResponse(
            from,
            to,
            branchId,
            paid.Count,
            paid.Sum(p => p.Amount),
            pending.Count,
            pending.Sum(p => p.Amount),
            stats.Count(s => s.Type == TransactionType.Charge && s.Status == TransactionStatus.Failed),
            byMethod,
            byProvider,
            daily,
            cod,
            settlements);
    }
}

/// <summary>The simulated provider's pages, available only in test mode (otherwise they do not exist).</summary>
internal sealed class TestGatewayService(
    ITestGatewaySessions sessions,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    IOptions<PaymentOptions> options,
    IValidator<TestGatewayCompleteRequest> validator) : ITestGatewayService
{
    public async Task<TestGatewaySessionResponse> GetSessionAsync(string reference, CancellationToken cancellationToken = default)
    {
        var session = await FindAsync(reference, cancellationToken).ConfigureAwait(false);
        return Map(session);
    }

    public async Task<TestGatewaySessionResponse> CompleteAsync(string reference, TestGatewayCompleteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await validator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var session = await FindAsync(reference, cancellationToken).ConfigureAwait(false);
        if (session.Status != TestSessionStatus.Pending)
        {
            throw new ConflictException($"This test payment is already {session.Status}.");
        }

        var outcome = request.Outcome.ToUpperInvariant() switch
        {
            "SUCCESS" => TestSessionStatus.Paid,
            "FAILURE" => TestSessionStatus.Failed,
            _ => TestSessionStatus.Cancelled,
        };
        session.Complete(outcome, clock.GetUtcNow(), outcome == TestSessionStatus.Paid ? TestGatewayIds.NewTransactionId() : null);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Map(session);
    }

    private async Task<TestGatewaySession> FindAsync(string reference, CancellationToken cancellationToken)
    {
        // Outside test mode there is no simulated provider: the pages simply do not exist.
        if (!options.Value.IsTest || !MerchantReferences.LooksValid(reference))
        {
            throw new NotFoundException("Page", "test payment");
        }

        return await sessions.GetByReferenceAsync(reference, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Page", "test payment");
    }

    private TestGatewaySessionResponse Map(TestGatewaySession session) =>
        new(
            session.MerchantReference,
            session.Provider,
            options.Value.TestMerchantName,
            session.Amount,
            session.Currency,
            session.Status.ToString(),
            session.ProviderTransactionId);
}
