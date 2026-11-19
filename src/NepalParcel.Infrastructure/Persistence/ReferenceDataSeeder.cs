using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Pricing;

namespace NepalParcel.Infrastructure.Persistence;

/// <summary>
/// Default reference data so a fresh environment can take bookings: a service area for each of Nepal's 77 districts and a
/// development price list. Runs only when <see cref="DatabaseOptions.SeedReferenceData"/> is on and only fills tables that are
/// empty, so it never overwrites prices someone has set. Real prices are managed through <c>/api/v1/pricing/rules</c>.
/// </summary>
public static partial class ReferenceDataSeeder
{
    private static readonly string[] ValleyDistricts = ["Kathmandu", "Lalitpur", "Bhaktapur"];

    private static readonly HashSet<string> HubDistricts = new(
        [
            "Chitwan", "Kaski", "Morang", "Sunsari", "Jhapa", "Rupandehi", "Banke", "Kailali", "Dhanusha", "Parsa", "Bara",
            "Makwanpur", "Dang", "Nawalpur",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> RemoteDistricts = new(
        ["Humla", "Mugu", "Dolpa", "Jumla", "Kalikot", "Bajhang", "Bajura", "Darchula", "Manang", "Mustang", "Taplejung"],
        StringComparer.OrdinalIgnoreCase);

    // Weight bands in kg: (min, max]. Price = base + perKg × (weight − min).
    private sealed record Band(decimal Min, decimal Max, decimal Base, decimal PerKg);

    private static readonly Band[] StandardNationwide = [new(0, 1, 150, 0), new(1, 5, 150, 45), new(5, 20, 330, 40), new(20, 100, 930, 35)];

    private static readonly Band[] StandardValley = [new(0, 1, 100, 0), new(1, 5, 100, 30), new(5, 20, 220, 25), new(20, 100, 595, 20)];

    private static readonly Band[] SameDayValley = [new(0, 2, 300, 0), new(2, 10, 300, 40), new(10, 20, 620, 35)];

    private const decimal ExpressMultiplier = 1.5m;

    public static async Task SeedReferenceDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        if (!scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.SeedReferenceData)
        {
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ReferenceDataSeeder));

        var (areas, rules) = await SeedAsync(db, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        LogSeeded(logger, areas, rules);
    }

    /// <summary>Fills empty tables and returns how many service areas and pricing rules were added.</summary>
    public static async Task<(int ServiceAreas, int PricingRules)> SeedAsync(
        NepalParcelDbContext db,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var areaCount = 0;
        if (!await db.ServiceAreas.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            var areas = CreateServiceAreas();
            db.ServiceAreas.AddRange(areas);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            areaCount = areas.Count;
        }

        var ruleCount = 0;
        if (!await db.PricingRules.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            var valley = await db.ServiceAreas
                .AsNoTracking()
                .Where(a => a.Municipality == null)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var valleyIds = valley
                .Where(a => ValleyDistricts.Contains(a.District, StringComparer.OrdinalIgnoreCase))
                .Select(a => a.Id)
                .ToList();

            var rules = CreateRules(valleyIds, nowUtc);
            db.PricingRules.AddRange(rules);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            ruleCount = rules.Count;
        }

        return (areaCount, ruleCount);
    }

    private static List<ServiceArea> CreateServiceAreas() =>
        NepalDistricts.All
            .Select(d => ServiceArea.Create(
                $"P{(int)d.Province}-{d.District.ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal)}",
                $"{d.District} District",
                d.Province,
                d.District,
                DeliveryDays(d.District)))
            .ToList();

    private static int DeliveryDays(string district)
    {
        if (ValleyDistricts.Contains(district, StringComparer.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (HubDistricts.Contains(district))
        {
            return 2;
        }

        return RemoteDistricts.Contains(district) ? 7 : 3;
    }

    private static List<PricingRule> CreateRules(IReadOnlyList<Guid> valleyAreaIds, DateTimeOffset nowUtc)
    {
        var rules = new List<PricingRule>();

        foreach (var band in StandardNationwide)
        {
            rules.Add(Rule("Standard nationwide", ServiceType.Standard, band, 1.5m, nowUtc));
            rules.Add(Rule("Express nationwide", ServiceType.Express, Scale(band), 1.5m, nowUtc));
        }

        // Kathmandu valley to Kathmandu valley is cheaper and is the only place same-day delivery is offered.
        foreach (var from in valleyAreaIds)
        {
            foreach (var to in valleyAreaIds)
            {
                foreach (var band in StandardValley)
                {
                    rules.Add(Rule("Standard valley", ServiceType.Standard, band, 1.5m, nowUtc, from, to));
                    rules.Add(Rule("Express valley", ServiceType.Express, Scale(band), 1.5m, nowUtc, from, to));
                }

                foreach (var band in SameDayValley)
                {
                    rules.Add(Rule("Same-day valley", ServiceType.SameDay, band, 2m, nowUtc, from, to));
                }
            }
        }

        return rules;
    }

    private static Band Scale(Band band) =>
        band with
        {
            Base = decimal.Round(band.Base * ExpressMultiplier, 2, MidpointRounding.AwayFromZero),
            PerKg = decimal.Round(band.PerKg * ExpressMultiplier, 2, MidpointRounding.AwayFromZero),
        };

    private static PricingRule Rule(
        string name,
        ServiceType serviceType,
        Band band,
        decimal codFeePercent,
        DateTimeOffset nowUtc,
        Guid? from = null,
        Guid? to = null) =>
        PricingRule.Create(
            $"{name} {band.Min:0.###}-{band.Max:0.###} kg",
            serviceType,
            band.Min,
            band.Max,
            band.Base,
            band.PerKg,
            codFeePercent,
            nowUtc,
            from,
            to);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Seeded {ServiceAreas} service area(s) and {PricingRules} pricing rule(s)")]
    private static partial void LogSeeded(ILogger logger, int serviceAreas, int pricingRules);
}
