using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Pricing;

/// <summary>A region (province / district / optional municipality) we deliver to, served by a branch.</summary>
public sealed class ServiceArea : AuditableEntity
{
    public const int CodeMaxLength = 20;

    private ServiceArea()
    {
        Code = string.Empty;
        Name = string.Empty;
        District = string.Empty;
    }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public Province Province { get; private set; }

    public string District { get; private set; }

    /// <summary>When null the rule covers the whole district.</summary>
    public string? Municipality { get; private set; }

    public Guid? ServingBranchId { get; private set; }

    public bool IsServiceable { get; private set; }

    public int EstimatedDeliveryDays { get; private set; }

    public static ServiceArea Create(
        string code,
        string name,
        Province province,
        string district,
        int estimatedDeliveryDays,
        string? municipality = null,
        Guid? servingBranchId = null)
    {
        if (!Enum.IsDefined(province))
        {
            throw new DomainException($"'{province}' is not a valid province of Nepal.");
        }

        if (estimatedDeliveryDays is < 0 or > 30)
        {
            throw new DomainException("Estimated delivery days must be between 0 and 30.");
        }

        return new ServiceArea
        {
            Code = Guard.NotBlank(code, "Code", CodeMaxLength).ToUpperInvariant(),
            Name = Guard.NotBlank(name, "Name", 150),
            Province = province,
            District = Guard.NotBlank(district, "District", NepalAddress.DistrictMaxLength),
            Municipality = Guard.Optional(municipality, "Municipality", NepalAddress.MunicipalityMaxLength),
            ServingBranchId = servingBranchId,
            EstimatedDeliveryDays = estimatedDeliveryDays,
            IsServiceable = true,
        };
    }

    public void SetServiceable(bool isServiceable) => IsServiceable = isServiceable;
}
