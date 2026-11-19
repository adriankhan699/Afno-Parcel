using System.Globalization;
using Microsoft.Extensions.Options;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Pricing;

namespace NepalParcel.Application.Parcels;

/// <summary>The outcome of pricing a route: the charge, the rule behind it and the delivery estimate.</summary>
internal sealed record PriceResult(
    PriceBreakdown Breakdown,
    PricingRule Rule,
    ServiceArea? OriginArea,
    ServiceArea DestinationArea,
    int EstimatedDays,
    DateTimeOffset EstimatedDeliveryAtUtc);

/// <summary>
/// The single place delivery charges are calculated. Both the price quote and the booking use it, so the quote a
/// customer sees is exactly what a booking would be charged at that moment. Nothing a client sends can change a price.
/// The caller supplies the instant, so the rules in force, the delivery estimate and the booking time all agree.
/// </summary>
internal sealed class PriceEngine(IPricingRepository pricing, IOptions<BookingOptions> options)
{
    public async Task<PriceResult> CalculateAsync(
        Branch originBranch,
        NepalAddress destination,
        ServiceType serviceType,
        decimal weightKg,
        decimal? lengthCm,
        decimal? widthCm,
        decimal? heightCm,
        decimal codGoodsAmount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(originBranch);
        ArgumentNullException.ThrowIfNull(destination);

        var settings = options.Value;

        var destinationArea = await ResolveAsync(destination, cancellationToken).ConfigureAwait(false);
        if (destinationArea is null || !destinationArea.IsServiceable)
        {
            throw new DomainException($"We do not deliver to {destination.District}, {destination.Province} Province yet.");
        }

        // The origin is where the parcel is handed over (a real branch), not an address the customer typed.
        var originArea = await ResolveAsync(originBranch.Address, cancellationToken).ConfigureAwait(false);

        var volumetric = PricingCalculator.VolumetricWeightKg(lengthCm, widthCm, heightCm, settings.VolumetricDivisor);
        var chargeable = PricingCalculator.ChargeableWeightKg(weightKg, volumetric, settings.WeightStepKg);

        var rules = await pricing.GetEffectiveRulesAsync(serviceType, now, cancellationToken).ConfigureAwait(false);
        var rule = PricingCalculator.SelectRule(rules, serviceType, originArea?.Id, destinationArea.Id, chargeable, now)
            ?? throw new DomainException(
                $"{serviceType} delivery is not available for this route at {chargeable.ToString("0.###", CultureInfo.InvariantCulture)} kg. " +
                "Please choose another service or contact a branch.");

        var breakdown = PricingCalculator.Calculate(rule, weightKg, volumetric, chargeable, codGoodsAmount, settings.VatPercent);
        return new PriceResult(
            breakdown,
            rule,
            originArea,
            destinationArea,
            DeliveryEstimator.EstimatedDays(serviceType, destinationArea.EstimatedDeliveryDays),
            DeliveryEstimator.Estimate(now, serviceType, destinationArea.EstimatedDeliveryDays));
    }

    private async Task<ServiceArea?> ResolveAsync(NepalAddress address, CancellationToken cancellationToken)
    {
        var areas = await pricing.GetServiceAreasInDistrictAsync(address.Province, address.District, cancellationToken).ConfigureAwait(false);
        return ServiceAreaResolver.Resolve(areas, address);
    }
}
