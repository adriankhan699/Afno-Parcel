using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Parcels;

/// <summary>A line item describing what is inside a parcel.</summary>
public sealed class ParcelItem : Entity
{
    public const int DescriptionMaxLength = 200;
    public const int CategoryMaxLength = 60;

    private ParcelItem()
    {
        Description = string.Empty;
    }

    internal ParcelItem(Guid parcelId, string description, string? category, int quantity, decimal weightKg, decimal declaredValue, bool isFragile)
    {
        if (quantity < 1)
        {
            throw new DomainException("Item quantity must be at least 1.");
        }

        ParcelId = parcelId;
        Description = Guard.NotBlank(description, "Item description", DescriptionMaxLength);
        Category = Guard.Optional(category, "Item category", CategoryMaxLength);
        Quantity = quantity;
        WeightKg = Guard.Positive(weightKg, "Item weight");
        DeclaredValue = Guard.NonNegative(declaredValue, "Item declared value");
        IsFragile = isFragile;
    }

    public Guid ParcelId { get; private init; }

    public string Description { get; private init; }

    public string? Category { get; private init; }

    public int Quantity { get; private init; }

    /// <summary>Weight of one unit in kilograms.</summary>
    public decimal WeightKg { get; private init; }

    /// <summary>Declared value of one unit in NPR.</summary>
    public decimal DeclaredValue { get; private init; }

    public bool IsFragile { get; private init; }
}
