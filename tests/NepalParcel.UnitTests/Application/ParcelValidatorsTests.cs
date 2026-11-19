using FluentValidation.Results;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Parcels;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;

namespace NepalParcel.UnitTests.Application;

public sealed class ParcelValidatorsTests
{
    private static readonly BookingOptions Settings = new();
    private readonly BookParcelRequestValidator _book = new(Options.Create(Settings));
    private readonly QuoteRequestValidator _quote = new(Options.Create(Settings));

    private static BookingPartyDto Party(string name = "Ram Sharma", string phone = "9841234567", AddressDto? address = null) =>
        new(name, phone, address ?? TestData.KathmanduAddressDto());

    private static BookParcelRequest ValidBooking() =>
        new(Party(), Party("Sita Gurung", "9851234567", TestData.PokharaAddressDto()), Guid.NewGuid(), "Books", 2, 2.5m, "Standard", "Cash");

    private static string[] Failed(ValidationResult result) => result.Errors.Select(e => e.PropertyName).Distinct().ToArray();

    // ---- booking -----------------------------------------------------------------------------------------------

    [Fact]
    public void Book_ValidRequest_Passes() => Assert.True(_book.Validate(ValidBooking()).IsValid);

    [Fact]
    public void Book_WithEveryOptionalField_Passes()
    {
        var request = ValidBooking() with
        {
            LengthCm = 30m,
            WidthCm = 20m,
            HeightCm = 10m,
            DeclaredValue = 5000m,
            CodAmount = 4000m,
            PaymentReference = "ESW-123",
            Notes = "Handle with care",
            Receiver = Party("Sita Gurung", "01-4412345", TestData.PokharaAddressDto()) with { AddressInstructions = "Blue gate, call first" },
        };

        Assert.True(_book.Validate(request).IsValid);
    }

    [Fact]
    public void Book_ReportsSenderAndReceiverProblemsSeparately()
    {
        var request = ValidBooking() with
        {
            Sender = Party(name: string.Empty),
            Receiver = Party(phone: "12345"),
        };

        var failed = Failed(_book.Validate(request));

        Assert.Contains("Sender.FullName", failed);
        Assert.Contains("Receiver.PhoneNumber", failed);
        Assert.DoesNotContain("Receiver.FullName", failed);
    }

    [Fact]
    public void Book_DistrictMustBelongToTheProvince()
    {
        var wrong = TestData.PokharaAddressDto() with { District = "Kathmandu" };

        var failed = Failed(_book.Validate(ValidBooking() with { Receiver = Party(address: wrong) }));

        Assert.Contains("Receiver.Address.District", failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100.5)]
    public void Book_WeightMustBePositiveAndWithinTheLimit(double weight) =>
        Assert.Contains("WeightKg", Failed(_book.Validate(ValidBooking() with { WeightKg = (decimal)weight })));

    [Fact]
    public void Book_WeightAtTheLimit_Passes() =>
        Assert.True(_book.Validate(ValidBooking() with { WeightKg = Settings.MaxWeightKg }).IsValid);

    [Fact]
    public void Book_WeightWithMoreThanThreeDecimals_Fails() =>
        Assert.Contains("WeightKg", Failed(_book.Validate(ValidBooking() with { WeightKg = 1.2345m })));

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-3)]
    public void Book_QuantityMustBeBetween1And100(int quantity) =>
        Assert.Contains("Quantity", Failed(_book.Validate(ValidBooking() with { Quantity = quantity })));

    [Theory]
    [InlineData(30, null, null)]
    [InlineData(30, 20, null)]
    [InlineData(null, 20, 10)]
    public void Book_DimensionsMustBeGivenTogetherOrNotAtAll(int? l, int? w, int? h)
    {
        var request = ValidBooking() with { LengthCm = (decimal?)l, WidthCm = (decimal?)w, HeightCm = (decimal?)h };

        Assert.Contains("Dimensions", Failed(_book.Validate(request)));
    }

    [Fact]
    public void Book_EachDimensionMustBePositiveAndNotTooLong()
    {
        var zero = _book.Validate(ValidBooking() with { LengthCm = 0m, WidthCm = 20m, HeightCm = 10m });
        var tooLong = _book.Validate(ValidBooking() with { LengthCm = 201m, WidthCm = 20m, HeightCm = 10m });

        Assert.Contains("LengthCm", Failed(zero));
        Assert.Contains("LengthCm", Failed(tooLong));
    }

    [Theory]
    [InlineData("Rocket")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("99")]
    public void Book_ServiceTypeMustBeANamedValue_NotANumber(string service) =>
        Assert.Contains("ServiceType", Failed(_book.Validate(ValidBooking() with { ServiceType = service })));

    [Theory]
    [InlineData("standard")]
    [InlineData("EXPRESS")]
    [InlineData("SameDay")]
    public void Book_ServiceTypeIsCaseInsensitive(string service) =>
        Assert.True(_book.Validate(ValidBooking() with { ServiceType = service }).IsValid);

    [Theory]
    [InlineData("Bitcoin")]
    [InlineData("2")]
    [InlineData("")]
    public void Book_PaymentMethodMustBeANamedValue(string method) =>
        Assert.Contains("PaymentMethod", Failed(_book.Validate(ValidBooking() with { PaymentMethod = method })));

    [Theory]
    [InlineData("Cash")]
    [InlineData("Esewa")]
    [InlineData("Khalti")]
    [InlineData("ConnectIps")]
    [InlineData("BankTransfer")]
    [InlineData("CashOnDelivery")]
    public void Book_AcceptsEveryPaymentMethod(string method) =>
        Assert.True(_book.Validate(ValidBooking() with { PaymentMethod = method }).IsValid);

    [Fact]
    public void Book_MoneyFields_MustBeNonNegativeAndWithinLimits()
    {
        Assert.Contains("DeclaredValue", Failed(_book.Validate(ValidBooking() with { DeclaredValue = -1m })));
        Assert.Contains("DeclaredValue", Failed(_book.Validate(ValidBooking() with { DeclaredValue = Settings.MaxDeclaredValue + 1 })));
        Assert.Contains("CodAmount", Failed(_book.Validate(ValidBooking() with { CodAmount = -1m })));
        Assert.Contains("CodAmount", Failed(_book.Validate(ValidBooking() with { CodAmount = Settings.MaxCodAmount + 1 })));
        Assert.Contains("CodAmount", Failed(_book.Validate(ValidBooking() with { CodAmount = 10.123m })));
        Assert.True(_book.Validate(ValidBooking() with { DeclaredValue = 0m, CodAmount = 0m }).IsValid);
    }

    [Fact]
    public void Book_TextFields_AreLengthLimited()
    {
        Assert.Contains("Description", Failed(_book.Validate(ValidBooking() with { Description = string.Empty })));
        Assert.Contains("Description", Failed(_book.Validate(ValidBooking() with { Description = new string('x', 201) })));
        Assert.Contains("Notes", Failed(_book.Validate(ValidBooking() with { Notes = new string('x', 501) })));
        Assert.Contains("PaymentReference", Failed(_book.Validate(ValidBooking() with { PaymentReference = new string('x', 101) })));
        Assert.Contains(
            "Receiver.AddressInstructions",
            Failed(_book.Validate(ValidBooking() with { Receiver = Party() with { AddressInstructions = new string('x', 301) } })));
    }

    [Fact]
    public void Book_OriginBranchIsRequired() =>
        Assert.Contains("OriginBranchId", Failed(_book.Validate(ValidBooking() with { OriginBranchId = Guid.Empty })));

    // ---- quote -------------------------------------------------------------------------------------------------

    private static QuoteRequest ValidQuote() => new(Guid.NewGuid(), TestData.PokharaAddressDto(), "Standard", 2.5m);

    [Fact]
    public void Quote_ValidRequest_Passes() => Assert.True(_quote.Validate(ValidQuote()).IsValid);

    [Fact]
    public void Quote_AppliesTheSameWeightDimensionAndServiceRulesAsBooking()
    {
        Assert.Contains("WeightKg", Failed(_quote.Validate(ValidQuote() with { WeightKg = 0m })));
        Assert.Contains("ServiceType", Failed(_quote.Validate(ValidQuote() with { ServiceType = "Rocket" })));
        Assert.Contains("Dimensions", Failed(_quote.Validate(ValidQuote() with { LengthCm = 10m })));
        Assert.Contains("OriginBranchId", Failed(_quote.Validate(ValidQuote() with { OriginBranchId = Guid.Empty })));
        Assert.Contains("Destination.District", Failed(_quote.Validate(ValidQuote() with { Destination = TestData.PokharaAddressDto() with { District = "Atlantis" } })));
    }

    // ---- search, cancel, confirm, pricing rules ----------------------------------------------------------------

    [Fact]
    public void Search_DefaultsAreValid() => Assert.True(new ParcelSearchQueryValidator().Validate(new ParcelSearchQuery()).IsValid);

    [Fact]
    public void Search_RejectsUnknownFilterValuesAndBadPaging()
    {
        var failed = Failed(new ParcelSearchQueryValidator().Validate(new ParcelSearchQuery
        {
            Page = 0,
            PageSize = 500,
            Status = "Teleported",
            ServiceType = "Rocket",
            PaymentMethod = "Bitcoin",
            Channel = "Phone",
            Search = new string('x', 101),
        }));

        Assert.Equal(
            new[] { "Page", "PageSize", "Search", "Status", "ServiceType", "PaymentMethod", "Channel" }.Order(StringComparer.Ordinal),
            failed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Search_AcceptsKnownFilterValues()
    {
        var query = new ParcelSearchQuery
        {
            Status = "InTransit",
            ServiceType = "express",
            PaymentMethod = "CashOnDelivery",
            Channel = "Online",
            BookedFrom = new DateOnly(2026, 11, 1),
            BookedTo = new DateOnly(2026, 11, 30),
        };

        Assert.True(new ParcelSearchQueryValidator().Validate(query).IsValid);
    }

    [Fact]
    public void Search_EndDateCannotBeBeforeStartDate()
    {
        var query = new ParcelSearchQuery { BookedFrom = new DateOnly(2026, 11, 10), BookedTo = new DateOnly(2026, 11, 9) };

        Assert.Contains("BookedTo", Failed(new ParcelSearchQueryValidator().Validate(query)));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("Changed my mind", true)]
    public void Cancel_RequiresAReasonOfAtLeastFiveCharacters(string reason, bool valid) =>
        Assert.Equal(valid, new CancelParcelRequestValidator().Validate(new CancelParcelRequest(reason)).IsValid);

    [Fact]
    public void Cancel_ReasonIsLimitedTo300Characters() =>
        Assert.False(new CancelParcelRequestValidator().Validate(new CancelParcelRequest(new string('x', 301))).IsValid);

    [Fact]
    public void ConfirmPayment_ReferenceIsOptionalButLimited()
    {
        var validator = new ConfirmPaymentRequestValidator();

        Assert.True(validator.Validate(new ConfirmPaymentRequest()).IsValid);
        Assert.True(validator.Validate(new ConfirmPaymentRequest("REC-1")).IsValid);
        Assert.False(validator.Validate(new ConfirmPaymentRequest(new string('x', 101))).IsValid);
    }

    [Fact]
    public void PricingRule_BandMustBeAscending_AndPricesSane()
    {
        var validator = new CreatePricingRuleRequestValidator();
        var valid = new CreatePricingRuleRequest("Standard 1-5 kg", "Standard", 1m, 5m, 150m, 45m, 1.5m);

        Assert.True(validator.Validate(valid).IsValid);
        Assert.Contains("MaxWeightKg", Failed(validator.Validate(valid with { MaxWeightKg = 1m })));
        Assert.Contains("MinWeightKg", Failed(validator.Validate(valid with { MinWeightKg = -1m })));
        Assert.Contains("BasePrice", Failed(validator.Validate(valid with { BasePrice = -1m })));
        Assert.Contains("PricePerKg", Failed(validator.Validate(valid with { PricePerKg = -0.01m })));
        Assert.Contains("CodFeePercent", Failed(validator.Validate(valid with { CodFeePercent = 100.5m })));
        Assert.Contains("ServiceType", Failed(validator.Validate(valid with { ServiceType = "Rocket" })));
        Assert.Contains("Name", Failed(validator.Validate(valid with { Name = string.Empty })));
    }

    [Fact]
    public void PricingRule_EndMustBeAfterStart()
    {
        var validator = new CreatePricingRuleRequestValidator();
        var start = TestData.Now;
        var request = new CreatePricingRuleRequest("Rule", "Standard", 0m, 5m, 100m, 10m, 1m, EffectiveFromUtc: start, EffectiveToUtc: start);

        Assert.Contains("EffectiveToUtc", Failed(validator.Validate(request)));
        Assert.True(validator.Validate(request with { EffectiveToUtc = start.AddDays(1) }).IsValid);
    }
}
