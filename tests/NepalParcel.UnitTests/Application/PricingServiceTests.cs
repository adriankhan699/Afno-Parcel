using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Parcels;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Pricing;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

public sealed class PricingServiceTests
{
    private readonly IBranchRepository _branches = Substitute.For<IBranchRepository>();
    private readonly IPricingRepository _pricing = Substitute.For<IPricingRepository>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IClientInfo _client = Substitute.For<IClientInfo>();
    private readonly FixedClock _clock = new(TestData.Now);
    private readonly Branch _origin = Branch.Create("KTM01", "Kathmandu Main", BranchType.Branch, TestData.KathmanduAddress(), "9841000000");
    private readonly ServiceArea _kathmandu = ServiceArea.Create("P3-KATHMANDU", "Kathmandu District", Province.Bagmati, "Kathmandu", 1);
    private readonly ServiceArea _pokhara = ServiceArea.Create("P4-KASKI", "Kaski District", Province.Gandaki, "Kaski", 3);
    private readonly PricingService _sut;

    public PricingServiceTests()
    {
        _branches.GetByIdAsync(_origin.Id, Arg.Any<CancellationToken>()).Returns(_origin);
        _pricing.GetServiceAreasInDistrictAsync(Province.Bagmati, "Kathmandu", Arg.Any<CancellationToken>()).Returns([_kathmandu]);
        _pricing.GetServiceAreasInDistrictAsync(Province.Gandaki, "Kaski", Arg.Any<CancellationToken>()).Returns([_pokhara]);
        _pricing.GetEffectiveRulesAsync(Arg.Any<ServiceType>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        _pricing.GetEffectiveRulesAsync(ServiceType.Standard, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([TestData.Rule(), TestData.Rule(fromArea: _kathmandu.Id, toArea: _pokhara.Id, basePrice: 120m, pricePerKg: 30m)]);
        _pricing.ServiceAreaExistsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _pricing.GetServiceAreaNamesAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [_kathmandu.Id] = "Kathmandu District", [_pokhara.Id] = "Kaski District" });
        _currentUser.UserId.Returns(Guid.NewGuid());

        var options = Options.Create(new BookingOptions());
        _sut = new PricingService(
            new PriceEngine(_pricing, options),
            _branches, _pricing, _audit, _unitOfWork, _currentUser, _client, _clock,
            new QuoteRequestValidator(options),
            new CreatePricingRuleRequestValidator(),
            new PageQueryValidator(),
            NullLogger<PricingService>.Instance);
    }

    private QuoteRequest Quote(string service = "Standard", decimal weight = 2.5m, decimal? cod = null) =>
        new(_origin.Id, TestData.PokharaAddressDto(), service, weight, CodAmount: cod);

    [Fact]
    public async Task Quote_UsesTheMostSpecificRule_AndExplainsTheCharge()
    {
        var quote = await _sut.QuoteAsync(Quote());

        // Kathmandu → Kaski rule: 120 + 30 × 1.5 = 165.00; VAT 13% = 21.45; total 186.45.
        Assert.Equal("Standard", quote.ServiceType);
        Assert.Equal("Kathmandu District", quote.OriginArea);
        Assert.Equal("Kaski District", quote.DestinationArea);
        Assert.Equal("NPR", quote.Currency);
        Assert.Equal(165m, quote.Breakdown.ShippingCharge);
        Assert.Equal(21.45m, quote.Breakdown.VatAmount);
        Assert.Equal(186.45m, quote.Breakdown.TotalCharge);
        Assert.Equal(3, quote.EstimatedDeliveryDays);
        Assert.Equal(TestData.Now.AddDays(3), quote.EstimatedDeliveryAtUtc);
    }

    [Fact]
    public async Task Quote_VolumetricWeightCanRaiseThePrice()
    {
        // 60 × 50 × 40 cm = 120 000 cm³ ÷ 5000 = 24 kg, far heavier than the 2 kg on the scale.
        _pricing.GetEffectiveRulesAsync(ServiceType.Standard, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([TestData.Rule(minKg: 20m, maxKg: 100m, basePrice: 930m, pricePerKg: 35m)]);

        var quote = await _sut.QuoteAsync(Quote(weight: 2m) with { LengthCm = 60m, WidthCm = 50m, HeightCm = 40m });

        Assert.Equal(2m, quote.Breakdown.ActualWeightKg);
        Assert.Equal(24m, quote.Breakdown.VolumetricWeightKg);
        Assert.Equal(24m, quote.Breakdown.ChargeableWeightKg);
        Assert.Equal(930m + (35m * 4m), quote.Breakdown.ShippingCharge);
    }

    [Fact]
    public async Task Quote_IncludesTheCodFee_WhenGoodsMoneyIsCollected()
    {
        var quote = await _sut.QuoteAsync(Quote(cod: 4000m));

        Assert.Equal(60m, quote.Breakdown.CodFee);
        Assert.Equal(quote.Breakdown.ShippingCharge + quote.Breakdown.CodFee + quote.Breakdown.VatAmount, quote.Breakdown.TotalCharge);
    }

    [Fact]
    public async Task Quote_OriginWithoutAServiceArea_StillGetsTheGenericRule()
    {
        _pricing.GetServiceAreasInDistrictAsync(Province.Bagmati, "Kathmandu", Arg.Any<CancellationToken>()).Returns([]);

        var quote = await _sut.QuoteAsync(Quote());

        Assert.Null(quote.OriginArea);
        Assert.Equal(217.5m, quote.Breakdown.ShippingCharge);
    }

    [Fact]
    public async Task Quote_ServiceWithoutARule_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => _sut.QuoteAsync(Quote(service: "Express")));

        Assert.Contains("Express delivery is not available", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quote_WeightBeyondEveryBand_IsRefused() =>
        await Assert.ThrowsAsync<DomainException>(() => _sut.QuoteAsync(Quote(weight: 50m)));

    [Fact]
    public async Task Quote_UnknownBranch_ThrowsNotFound() =>
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.QuoteAsync(Quote() with { OriginBranchId = Guid.NewGuid() }));

    [Fact]
    public async Task Quote_InvalidRequest_ThrowsValidationException() =>
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.QuoteAsync(Quote(weight: 0m)));

    [Fact]
    public async Task GetServiceAreas_AreOrderedByProvinceThenDistrict()
    {
        _pricing.GetServiceAreasAsync(Arg.Any<CancellationToken>()).Returns([_pokhara, _kathmandu]);

        var areas = await _sut.GetServiceAreasAsync();

        Assert.Equal(["Kathmandu", "Kaski"], areas.Select(a => a.District).ToArray());
        Assert.Equal("Bagmati", areas[0].Province);
    }

    [Fact]
    public async Task CreateRule_AddsTheRule_AndAuditsIt()
    {
        PricingRule? added = null;
        _pricing.When(p => p.AddRule(Arg.Any<PricingRule>())).Do(c => added = c.Arg<PricingRule>());

        var response = await _sut.CreateRuleAsync(
            new CreatePricingRuleRequest("Standard 5-20 kg", "Standard", 5m, 20m, 330m, 40m, 1.5m, Priority: 2, ToServiceAreaId: _pokhara.Id));

        Assert.NotNull(added);
        Assert.Equal(TestData.Now, added.EffectiveFromUtc);
        Assert.True(added.IsActive);
        Assert.Equal("Standard", response.ServiceType);
        Assert.Equal("Kaski District", response.ToServiceAreaName);
        _audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.PricingRuleCreated));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRule_UnknownServiceArea_ThrowsNotFound_AndSavesNothing()
    {
        _pricing.ServiceAreaExistsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.CreateRuleAsync(
            new CreatePricingRuleRequest("Rule", "Standard", 0m, 5m, 100m, 10m, 1m, FromServiceAreaId: Guid.NewGuid())));

        _pricing.DidNotReceive().AddRule(Arg.Any<PricingRule>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRule_InvalidRequest_ThrowsValidationException() =>
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.CreateRuleAsync(
            new CreatePricingRuleRequest("Rule", "Standard", 5m, 1m, 100m, 10m, 1m)));

    [Fact]
    public async Task DeactivateRule_SwitchesItOffFromNow()
    {
        var rule = TestData.Rule();
        _pricing.GetRuleByIdAsync(rule.Id, Arg.Any<CancellationToken>()).Returns(rule);

        await _sut.DeactivateRuleAsync(rule.Id);

        Assert.False(rule.IsActive);
        Assert.Equal(TestData.Now, rule.EffectiveToUtc);
        _audit.Received(1).Add(Arg.Is<AuditLog>(a => a.Action == AuditActions.PricingRuleDeactivated));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeactivateRule_AlreadyInactive_IsAConflict()
    {
        var rule = TestData.Rule();
        rule.Deactivate(TestData.Now.AddDays(-1));
        _pricing.GetRuleByIdAsync(rule.Id, Arg.Any<CancellationToken>()).Returns(rule);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.DeactivateRuleAsync(rule.Id));
    }

    [Fact]
    public async Task DeactivateRule_Unknown_ThrowsNotFound() =>
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.DeactivateRuleAsync(Guid.NewGuid()));

    [Fact]
    public async Task GetRules_ReturnsAPage_WithAreaNames()
    {
        var rule = TestData.Rule(toArea: _pokhara.Id);
        _pricing.GetRulesPageAsync(1, 20, Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<PricingRule>)[rule], 1));

        var page = await _sut.GetRulesAsync(new PageQuery());

        var item = Assert.Single(page.Items);
        Assert.Equal("Kaski District", item.ToServiceAreaName);
        Assert.Equal(1, page.TotalCount);
    }
}
