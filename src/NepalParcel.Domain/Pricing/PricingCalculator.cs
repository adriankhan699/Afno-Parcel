using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.Domain.Pricing;

/// <summary>How a delivery charge was built up. All money is NPR rounded to paisa.</summary>
public sealed record PriceBreakdown(
    decimal ActualWeightKg,
    decimal VolumetricWeightKg,
    decimal ChargeableWeightKg,
    decimal BaseCharge,
    decimal WeightCharge,
    decimal ShippingCharge,
    decimal CodFee,
    decimal VatPercent,
    decimal VatAmount,
    decimal TotalCharge);

/// <summary>
/// Pure pricing rules. The server is the only place a delivery charge is ever calculated; nothing here depends on
/// what a client sends. Rules themselves are data (<see cref="PricingRule"/>), so prices change without a deployment.
/// </summary>
public static class PricingCalculator
{
    /// <summary>Common courier divisor: volumetric kg = length × width × height (cm) ÷ 5000.</summary>
    public const decimal DefaultVolumetricDivisor = 5000m;

    /// <summary>Weight is priced in half-kilogram steps.</summary>
    public const decimal DefaultWeightStepKg = 0.5m;

    /// <summary>Nepal's value added tax rate on services.</summary>
    public const decimal DefaultVatPercent = 13m;

    /// <summary>Weight implied by the parcel's size; 0 when no dimensions were given.</summary>
    public static decimal VolumetricWeightKg(decimal? lengthCm, decimal? widthCm, decimal? heightCm, decimal divisor = DefaultVolumetricDivisor)
    {
        if (divisor <= 0)
        {
            throw new DomainException("Volumetric divisor must be greater than zero.");
        }

        if (lengthCm is null && widthCm is null && heightCm is null)
        {
            return 0m;
        }

        if (lengthCm is not > 0 || widthCm is not > 0 || heightCm is not > 0)
        {
            throw new DomainException("Length, width and height must all be given and greater than zero.");
        }

        return decimal.Round(lengthCm.Value * widthCm.Value * heightCm.Value / divisor, 3, MidpointRounding.AwayFromZero);
    }

    /// <summary>The weight we charge for: the larger of actual and volumetric, rounded up to the next step.</summary>
    public static decimal ChargeableWeightKg(decimal actualWeightKg, decimal volumetricWeightKg, decimal stepKg = DefaultWeightStepKg)
    {
        if (actualWeightKg <= 0)
        {
            throw new DomainException("Weight must be greater than zero.");
        }

        if (stepKg <= 0)
        {
            throw new DomainException("Weight step must be greater than zero.");
        }

        var heavier = Math.Max(actualWeightKg, volumetricWeightKg);
        return Math.Ceiling(heavier / stepKg) * stepKg;
    }

    /// <summary>
    /// Picks the rule that prices a parcel: it must be active and in date, match the service type and route, and cover
    /// the weight. The most specific rule wins (names both areas &gt; one area &gt; neither), then the highest priority,
    /// then the newest, then the lowest id so the result is always deterministic. Returns null if nothing matches.
    /// </summary>
    public static PricingRule? SelectRule(
        IEnumerable<PricingRule> rules,
        ServiceType serviceType,
        Guid? originAreaId,
        Guid? destinationAreaId,
        decimal chargeableWeightKg,
        DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(rules);

        return rules
            .Where(r => r.ServiceType == serviceType
                        && r.IsEffectiveAt(atUtc)
                        && r.MatchesRoute(originAreaId, destinationAreaId)
                        && r.CoversWeight(chargeableWeightKg))
            .OrderByDescending(r => r.Specificity)
            .ThenByDescending(r => r.Priority)
            .ThenByDescending(r => r.EffectiveFromUtc)
            .ThenBy(r => r.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Price = base + per-kg charge above the band start, plus a COD fee on goods money collected, plus VAT on both.
    /// </summary>
    /// <param name="rule">The rule chosen by <see cref="SelectRule"/>.</param>
    /// <param name="actualWeightKg">Weight entered by the sender.</param>
    /// <param name="volumetricWeightKg">Weight implied by size (0 if unknown).</param>
    /// <param name="chargeableWeightKg">Weight priced (from <see cref="ChargeableWeightKg"/>).</param>
    /// <param name="codGoodsAmount">Goods money (NPR) the receiver pays on delivery on behalf of the sender; 0 if none.</param>
    /// <param name="vatPercent">VAT rate, 0–100.</param>
    public static PriceBreakdown Calculate(
        PricingRule rule,
        decimal actualWeightKg,
        decimal volumetricWeightKg,
        decimal chargeableWeightKg,
        decimal codGoodsAmount,
        decimal vatPercent)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.CoversWeight(chargeableWeightKg))
        {
            throw new DomainException("The pricing rule does not cover this weight.");
        }

        if (codGoodsAmount < 0)
        {
            throw new DomainException("COD amount cannot be negative.");
        }

        if (vatPercent is < 0 or > 100)
        {
            throw new DomainException("VAT percent must be between 0 and 100.");
        }

        var baseCharge = Round(rule.BasePrice);
        var weightCharge = Round(rule.PricePerKg * (chargeableWeightKg - rule.MinWeightKg));
        var shipping = baseCharge + weightCharge;
        var codFee = codGoodsAmount > 0 ? Round(codGoodsAmount * rule.CodFeePercent / 100m) : 0m;
        var vat = Round((shipping + codFee) * vatPercent / 100m);

        return new PriceBreakdown(
            Round(actualWeightKg),
            volumetricWeightKg,
            chargeableWeightKg,
            baseCharge,
            weightCharge,
            shipping,
            codFee,
            vatPercent,
            vat,
            shipping + codFee + vat);
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
