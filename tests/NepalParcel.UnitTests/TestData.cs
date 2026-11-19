using NepalParcel.Contracts.Branches;
using NepalParcel.Contracts.Common;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;
using NepalParcel.Domain.Pricing;

namespace NepalParcel.UnitTests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 17, 6, 0, 0, TimeSpan.Zero);

    public static NepalAddress KathmanduAddress() =>
        NepalAddress.Create(Province.Bagmati, "Kathmandu", "Kathmandu Metropolitan City", 10, "New Baneshwor", "Near Everest Hotel", "44600");

    public static NepalAddress PokharaAddress() =>
        NepalAddress.Create(Province.Gandaki, "Kaski", "Pokhara Metropolitan City", 8, "Lakeside");

    public static AddressDto KathmanduAddressDto() =>
        new("Bagmati", "Kathmandu", "Kathmandu Metropolitan City", 10, "New Baneshwor", null, "44600");

    public static AddressDto PokharaAddressDto() =>
        new("Gandaki", "Kaski", "Pokhara Metropolitan City", 8, "Lakeside");

    public static CreateBranchRequest ValidCreateBranchRequest(string code = "KTM01") =>
        new(code, "Kathmandu Main Branch", "Branch", KathmanduAddressDto(), "9841000000", "ktm@nepalparcel.test");

    /// <summary>A pricing rule. Defaults to the 1–5 kg Standard band: NPR 150 + NPR 45 per kg above 1 kg, 1.5% COD fee.</summary>
    public static PricingRule Rule(
        ServiceType serviceType = ServiceType.Standard,
        decimal minKg = 1m,
        decimal maxKg = 5m,
        decimal basePrice = 150m,
        decimal pricePerKg = 45m,
        decimal codFeePercent = 1.5m,
        Guid? fromArea = null,
        Guid? toArea = null,
        int priority = 0,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null) =>
        PricingRule.Create(
            "Test rule", serviceType, minKg, maxKg, basePrice, pricePerKg, codFeePercent,
            from ?? Now.AddDays(-30), fromArea, toArea, to, priority);

    /// <summary>A consistent price for a 2.5 kg parcel on the default rule, with an optional COD goods amount.</summary>
    public static PriceBreakdown Price(decimal weightKg = 2.5m, decimal codGoods = 0m, decimal vatPercent = 13m) =>
        PricingCalculator.Calculate(Rule(), weightKg, 0m, weightKg, codGoods, vatPercent);

    public static NewParcelDetails ParcelDetails(
        PaymentMethod method = PaymentMethod.Cash,
        decimal codGoods = 0m,
        PriceBreakdown? price = null,
        string? trackingNumber = null,
        BookingChannel channel = BookingChannel.Counter) =>
        new()
        {
            TrackingNumber = trackingNumber ?? TrackingNumbers.Generate(),
            SenderCustomerId = Guid.NewGuid(),
            SenderName = "Ram Sharma",
            SenderPhone = "9841234567",
            SenderAddress = KathmanduAddress(),
            ReceiverName = "Sita Gurung",
            ReceiverPhone = "9851234567",
            ReceiverAddress = PokharaAddress(),
            ReceiverAddressInstructions = "Blue gate",
            OriginBranchId = Guid.NewGuid(),
            DestinationBranchId = Guid.NewGuid(),
            ServiceType = ServiceType.Standard,
            Channel = channel,
            Description = "Books",
            Quantity = 2,
            WeightKg = 2.5m,
            DeclaredValue = 5000m,
            PaymentMethod = method,
            Price = price ?? Price(codGoods: codGoods),
            CodGoodsAmount = codGoods,
        };

    public static Parcel NewParcel(PaymentMethod method = PaymentMethod.Cash, decimal codGoods = 0m) =>
        Parcel.Book(ParcelDetails(method, codGoods), Now, bookedByName: "Counter staff");
}

/// <summary>A clock that always returns the same instant.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
