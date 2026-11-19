using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.Domain.Pricing;

/// <summary>
/// A delivery price rule in NPR. The most specific active rule (matching service type, areas and weight band, then
/// highest <see cref="Priority"/>) is applied by the pricing service in a later phase. Rules are never overwritten
/// for past dates: change a price by ending the old rule and adding a new one, so historic quotes stay explainable.
/// </summary>
public sealed class PricingRule : AuditableEntity
{
    private PricingRule()
    {
        Name = string.Empty;
    }

    public string Name { get; private set; }

    public ServiceType ServiceType { get; private set; }

    /// <summary>Null matches any origin area.</summary>
    public Guid? FromServiceAreaId { get; private set; }

    /// <summary>Null matches any destination area.</summary>
    public Guid? ToServiceAreaId { get; private set; }

    public decimal MinWeightKg { get; private set; }

    public decimal MaxWeightKg { get; private set; }

    /// <summary>Flat charge in NPR for the weight band.</summary>
    public decimal BasePrice { get; private set; }

    /// <summary>Extra NPR per kg above <see cref="MinWeightKg"/>.</summary>
    public decimal PricePerKg { get; private set; }

    /// <summary>Cash-on-delivery fee as a percentage (0–100) of the COD amount.</summary>
    public decimal CodFeePercent { get; private set; }

    public DateTimeOffset EffectiveFromUtc { get; private set; }

    public DateTimeOffset? EffectiveToUtc { get; private set; }

    public int Priority { get; private set; }

    public bool IsActive { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static PricingRule Create(
        string name,
        ServiceType serviceType,
        decimal minWeightKg,
        decimal maxWeightKg,
        decimal basePrice,
        decimal pricePerKg,
        decimal codFeePercent,
        DateTimeOffset effectiveFromUtc,
        Guid? fromServiceAreaId = null,
        Guid? toServiceAreaId = null,
        DateTimeOffset? effectiveToUtc = null,
        int priority = 0)
    {
        if (!Enum.IsDefined(serviceType))
        {
            throw new DomainException($"'{serviceType}' is not a valid service type.");
        }

        if (minWeightKg < 0 || maxWeightKg <= minWeightKg)
        {
            throw new DomainException("Weight band must satisfy 0 <= min < max.");
        }

        if (codFeePercent is < 0 or > 100)
        {
            throw new DomainException("COD fee percent must be between 0 and 100.");
        }

        if (effectiveToUtc is not null && effectiveToUtc <= effectiveFromUtc)
        {
            throw new DomainException("Effective end must be after the start.");
        }

        return new PricingRule
        {
            Name = Guard.NotBlank(name, "Name", 150),
            ServiceType = serviceType,
            FromServiceAreaId = fromServiceAreaId,
            ToServiceAreaId = toServiceAreaId,
            MinWeightKg = decimal.Round(minWeightKg, 3),
            MaxWeightKg = decimal.Round(maxWeightKg, 3),
            BasePrice = Guard.NonNegative(basePrice, "Base price"),
            PricePerKg = Guard.NonNegative(pricePerKg, "Price per kg"),
            CodFeePercent = decimal.Round(codFeePercent, 2),
            EffectiveFromUtc = effectiveFromUtc,
            EffectiveToUtc = effectiveToUtc,
            Priority = priority,
            IsActive = true,
        };
    }

    public void Deactivate(DateTimeOffset endsAtUtc)
    {
        IsActive = false;
        EffectiveToUtc ??= endsAtUtc;
    }

    /// <summary>Whether the rule is switched on and inside its validity period at <paramref name="atUtc"/>.</summary>
    public bool IsEffectiveAt(DateTimeOffset atUtc) =>
        IsActive && EffectiveFromUtc <= atUtc && (EffectiveToUtc is null || atUtc < EffectiveToUtc);

    /// <summary>A weight band covers weights above its minimum, up to and including its maximum.</summary>
    public bool CoversWeight(decimal weightKg) => weightKg > MinWeightKg && weightKg <= MaxWeightKg;

    /// <summary>Whether the rule applies to the route. A null area on the rule means "any".</summary>
    public bool MatchesRoute(Guid? originAreaId, Guid? destinationAreaId) =>
        (FromServiceAreaId is null || FromServiceAreaId == originAreaId) &&
        (ToServiceAreaId is null || ToServiceAreaId == destinationAreaId);

    /// <summary>0, 1 or 2: how many of the origin and destination this rule names explicitly. More specific rules win.</summary>
    public int Specificity => (FromServiceAreaId is null ? 0 : 1) + (ToServiceAreaId is null ? 0 : 1);
}
