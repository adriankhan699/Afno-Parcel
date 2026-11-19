using System.ComponentModel.DataAnnotations;
using NepalParcel.Domain.Pricing;

namespace NepalParcel.Application.Parcels;

/// <summary>
/// Booking and pricing settings (configuration section <c>Booking</c>). Prices per route and weight live in the
/// database as pricing rules; these are the settings that apply to every booking.
/// </summary>
public sealed class BookingOptions
{
    public const string SectionName = "Booking";

    /// <summary>VAT added to shipping and COD fees, as a percentage.</summary>
    [Range(typeof(decimal), "0", "100")]
    public decimal VatPercent { get; set; } = PricingCalculator.DefaultVatPercent;

    /// <summary>Volumetric weight (kg) = length × width × height (cm) ÷ this number.</summary>
    [Range(typeof(decimal), "100", "100000")]
    public decimal VolumetricDivisor { get; set; } = PricingCalculator.DefaultVolumetricDivisor;

    /// <summary>Chargeable weight is rounded up to a multiple of this many kilograms.</summary>
    [Range(typeof(decimal), "0.1", "10")]
    public decimal WeightStepKg { get; set; } = PricingCalculator.DefaultWeightStepKg;

    /// <summary>Heaviest parcel that can be booked.</summary>
    [Range(typeof(decimal), "1", "5000")]
    public decimal MaxWeightKg { get; set; } = 100m;

    /// <summary>Longest side of a parcel in centimetres.</summary>
    [Range(typeof(decimal), "1", "1000")]
    public decimal MaxDimensionCm { get; set; } = 200m;

    /// <summary>Highest declared value in NPR.</summary>
    [Range(typeof(decimal), "0", "1000000000")]
    public decimal MaxDeclaredValue { get; set; } = 500_000m;

    /// <summary>Highest goods amount in NPR the rider may be asked to collect.</summary>
    [Range(typeof(decimal), "0", "1000000000")]
    public decimal MaxCodAmount { get; set; } = 200_000m;

    public ReceiptOptions Receipt { get; set; } = new();
}

/// <summary>Details printed in the header of booking receipts (configuration section <c>Booking:Receipt</c>).</summary>
public sealed class ReceiptOptions
{
    [Required]
    [StringLength(150)]
    public string CompanyName { get; set; } = "Afno Parcel";

    [StringLength(30)]
    public string? CompanyPhone { get; set; }

    /// <summary>PAN or VAT registration number to print on receipts.</summary>
    [StringLength(30)]
    public string? CompanyTaxNumber { get; set; }
}
