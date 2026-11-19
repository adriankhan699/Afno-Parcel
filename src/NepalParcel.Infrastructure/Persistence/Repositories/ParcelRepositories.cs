using Microsoft.EntityFrameworkCore;
using NepalParcel.Application.Parcels;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;
using NepalParcel.Domain.Pricing;

namespace NepalParcel.Infrastructure.Persistence.Repositories;

internal sealed class ParcelRepository(NepalParcelDbContext dbContext) : IParcelRepository
{
    private const int MinTextSearchLength = 3;

    public void Add(Parcel parcel) => dbContext.Parcels.Add(parcel);

    public Task<Parcel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Parcels.Include(p => p.StatusHistory).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Parcel?> GetByTrackingNumberAsync(string trackingNumber, CancellationToken cancellationToken = default) =>
        dbContext.Parcels.Include(p => p.StatusHistory).FirstOrDefaultAsync(p => p.TrackingNumber == trackingNumber, cancellationToken);

    public Task<bool> TrackingNumberExistsAsync(string trackingNumber, CancellationToken cancellationToken = default) =>
        dbContext.Parcels.AnyAsync(p => p.TrackingNumber == trackingNumber, cancellationToken);

    public async Task<(IReadOnlyList<ParcelListItem> Items, int TotalCount)> SearchAsync(
        ParcelFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = ApplyFilter(dbContext.Parcels.AsNoTracking(), filter);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await (
                from p in query
                join b in dbContext.Branches.AsNoTracking() on p.OriginBranchId equals b.Id
                orderby p.BookedAtUtc descending, p.Id descending
                select new ParcelListItem(
                    p.Id,
                    p.TrackingNumber,
                    p.CurrentStatusId,
                    p.ServiceType,
                    p.Channel,
                    p.PaymentMethod,
                    dbContext.Payments
                        .Where(pay => pay.ParcelId == p.Id)
                        .OrderByDescending(pay => pay.CreatedAtUtc)
                        .Select(pay => (PaymentStatus?)pay.Status)
                        .FirstOrDefault(),
                    p.SenderName,
                    p.ReceiverName,
                    p.ReceiverAddress.District,
                    p.ReceiverAddress.Municipality,
                    b.Name,
                    p.Quantity,
                    p.WeightKg,
                    p.DeliveryCharge,
                    p.CodAmount,
                    p.BookedAtUtc))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, total);
    }

    private static IQueryable<Parcel> ApplyFilter(IQueryable<Parcel> query, ParcelFilter filter)
    {
        if (filter.SenderCustomerId is { } customerId)
        {
            query = query.Where(p => p.SenderCustomerId == customerId);
        }

        if (filter.Status is { } status)
        {
            var statusId = (int)status;
            query = query.Where(p => p.CurrentStatusId == statusId);
        }

        if (filter.ServiceType is { } serviceType)
        {
            query = query.Where(p => p.ServiceType == serviceType);
        }

        if (filter.PaymentMethod is { } method)
        {
            query = query.Where(p => p.PaymentMethod == method);
        }

        if (filter.Channel is { } channel)
        {
            query = query.Where(p => p.Channel == channel);
        }

        if (filter.IsCod is true)
        {
            query = query.Where(p => p.CodAmount > 0m);
        }
        else if (filter.IsCod is false)
        {
            query = query.Where(p => p.CodAmount <= 0m);
        }

        if (filter.OriginBranchId is { } branchId)
        {
            query = query.Where(p => p.OriginBranchId == branchId);
        }

        if (filter.BookedFromUtc is { } from)
        {
            query = query.Where(p => p.BookedAtUtc >= from);
        }

        if (filter.BookedBeforeUtc is { } before)
        {
            query = query.Where(p => p.BookedAtUtc < before);
        }

        return string.IsNullOrWhiteSpace(filter.Search) ? query : ApplySearch(query, filter.Search);
    }

    /// <summary>Matches a tracking number prefix, part of a sender/receiver name, or part of a phone number.</summary>
    private static IQueryable<Parcel> ApplySearch(IQueryable<Parcel> query, string search)
    {
        var term = search.Trim();
        var tracking = TrackingNumbers.Normalize(term);
        var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
        var like = $"%{EscapeLike(term)}%";

        var useTracking = tracking.Length >= MinTextSearchLength;
        var usePhone = digits.Length >= MinTextSearchLength;

        return query.Where(p =>
            (useTracking && p.TrackingNumber.StartsWith(tracking))
            || EF.Functions.Like(p.SenderName, like, "\\")
            || EF.Functions.Like(p.ReceiverName, like, "\\")
            || (usePhone && (p.SenderPhone.Contains(digits) || p.ReceiverPhone.Contains(digits))));
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);
}

internal sealed class PaymentRepository(NepalParcelDbContext dbContext) : IPaymentRepository
{
    public void Add(Payment payment) => dbContext.Payments.Add(payment);

    public Task<Payment?> GetLatestByParcelIdAsync(Guid parcelId, CancellationToken cancellationToken = default) =>
        dbContext.Payments
            .Where(p => p.ParcelId == parcelId)
            .OrderByDescending(p => p.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
}

internal sealed class PricingRepository(NepalParcelDbContext dbContext) : IPricingRepository
{
    public async Task<IReadOnlyList<ServiceArea>> GetServiceAreasAsync(CancellationToken cancellationToken = default) =>
        await dbContext.ServiceAreas.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ServiceArea>> GetServiceAreasInDistrictAsync(
        Province province,
        string district,
        CancellationToken cancellationToken = default)
    {
        var name = district.Trim().ToLowerInvariant();
        return await dbContext.ServiceAreas
            .AsNoTracking()
            .Where(a => a.Province == province && a.District.ToLower() == name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<bool> ServiceAreaExistsAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ServiceAreas.AnyAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PricingRule>> GetEffectiveRulesAsync(
        ServiceType serviceType,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default) =>
        await dbContext.PricingRules
            .AsNoTracking()
            .Where(r => r.ServiceType == serviceType
                        && r.IsActive
                        && r.EffectiveFromUtc <= atUtc
                        && (r.EffectiveToUtc == null || r.EffectiveToUtc > atUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<(IReadOnlyList<PricingRule> Items, int TotalCount)> GetRulesPageAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.PricingRules.AsNoTracking();
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderBy(r => r.ServiceType)
            .ThenBy(r => r.Name)
            .ThenByDescending(r => r.EffectiveFromUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, total);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetServiceAreaNamesAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        var wanted = ids.Distinct().ToList();
        return await dbContext.ServiceAreas
            .AsNoTracking()
            .Where(a => wanted.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<PricingRule?> GetRuleByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.PricingRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void AddRule(PricingRule rule) => dbContext.PricingRules.Add(rule);
}
