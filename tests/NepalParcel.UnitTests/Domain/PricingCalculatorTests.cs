using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Pricing;

namespace NepalParcel.UnitTests.Domain;

public sealed class PricingCalculatorTests
{
    private static readonly DateTimeOffset Now = TestData.Now;

    // ---- volumetric and chargeable weight ----------------------------------------------------------------------

    [Fact]
    public void VolumetricWeight_IsZero_WhenNoDimensionsGiven() =>
        Assert.Equal(0m, PricingCalculator.VolumetricWeightKg(null, null, null));

    [Fact]
    public void VolumetricWeight_DividesVolumeByFiveThousand() =>
        Assert.Equal(12m, PricingCalculator.VolumetricWeightKg(50m, 40m, 30m));

    [Fact]
    public void VolumetricWeight_UsesTheConfiguredDivisor() =>
        Assert.Equal(6m, PricingCalculator.VolumetricWeightKg(50m, 40m, 30m, divisor: 10_000m));

    [Theory]
    [InlineData(50, null, null)]
    [InlineData(50, 40, null)]
    [InlineData(0, 40, 30)]
    [InlineData(-5, 40, 30)]
    public void VolumetricWeight_PartialOrNonPositiveDimensions_Throw(int? length, int? width, int? height) =>
        Assert.Throws<DomainException>(() => PricingCalculator.VolumetricWeightKg((decimal?)length, (decimal?)width, (decimal?)height));

    [Fact]
    public void VolumetricWeight_NonPositiveDivisor_Throws() =>
        Assert.Throws<DomainException>(() => PricingCalculator.VolumetricWeightKg(10m, 10m, 10m, 0m));

    [Theory]
    [InlineData(0.3, 0, 0.5)]
    [InlineData(1.0, 0, 1.0)]
    [InlineData(1.01, 0, 1.5)]
    [InlineData(2.3, 0, 2.5)]
    [InlineData(2.5, 0, 2.5)]
    [InlineData(2.5, 12, 12.0)]
    [InlineData(2.5, 12.2, 12.5)]
    [InlineData(20, 3, 20.0)]
    public void ChargeableWeight_IsTheHeavierWeight_RoundedUpToHalfAKilo(double actual, double volumetric, double expected) =>
        Assert.Equal((decimal)expected, PricingCalculator.ChargeableWeightKg((decimal)actual, (decimal)volumetric));

    [Fact]
    public void ChargeableWeight_RoundsUpToTheConfiguredStep() =>
        Assert.Equal(3m, PricingCalculator.ChargeableWeightKg(2.1m, 0m, stepKg: 1m));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ChargeableWeight_NonPositiveWeight_Throws(double weight) =>
        Assert.Throws<DomainException>(() => PricingCalculator.ChargeableWeightKg((decimal)weight, 0m));

    // ---- choosing the rule -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.5, "first")]
    [InlineData(1.0, "first")]   // a band includes its maximum...
    [InlineData(1.5, "second")]  // ...and excludes its minimum
    [InlineData(5.0, "second")]
    [InlineData(5.5, "third")]
    public void SelectRule_PicksTheBandThatCoversTheWeight(double weight, string expected)
    {
        var rules = new[]
        {
            (Label: "first", Rule: TestData.Rule(minKg: 0m, maxKg: 1m)),
            (Label: "second", Rule: TestData.Rule(minKg: 1m, maxKg: 5m)),
            (Label: "third", Rule: TestData.Rule(minKg: 5m, maxKg: 20m)),
        };

        var rule = PricingCalculator.SelectRule(rules.Select(r => r.Rule), ServiceType.Standard, null, null, (decimal)weight, Now);

        Assert.Equal(expected, rules.Single(r => r.Rule.Id == rule!.Id).Label);
    }

    [Fact]
    public void SelectRule_WeightAboveEveryBand_ReturnsNull()
    {
        var rule = PricingCalculator.SelectRule([TestData.Rule(minKg: 0m, maxKg: 5m)], ServiceType.Standard, null, null, 5.5m, Now);

        Assert.Null(rule);
    }

    [Fact]
    public void SelectRule_IgnoresOtherServiceTypes()
    {
        var express = TestData.Rule(ServiceType.Express);

        Assert.Null(PricingCalculator.SelectRule([express], ServiceType.Standard, null, null, 2m, Now));
        Assert.Same(express, PricingCalculator.SelectRule([express], ServiceType.Express, null, null, 2m, Now));
    }

    [Fact]
    public void SelectRule_PrefersTheMostSpecificRoute()
    {
        var origin = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var anywhere = TestData.Rule(priority: 100);
        var toDestination = TestData.Rule(toArea: destination);
        var fromOrigin = TestData.Rule(fromArea: origin);
        var exactRoute = TestData.Rule(fromArea: origin, toArea: destination);

        Assert.Same(exactRoute, PricingCalculator.SelectRule([anywhere, toDestination, fromOrigin, exactRoute], ServiceType.Standard, origin, destination, 2m, Now));
        Assert.Same(toDestination, PricingCalculator.SelectRule([anywhere, toDestination], ServiceType.Standard, origin, destination, 2m, Now));
        Assert.Same(anywhere, PricingCalculator.SelectRule([anywhere, toDestination], ServiceType.Standard, origin, Guid.NewGuid(), 2m, Now));
    }

    [Fact]
    public void SelectRule_RulesForOtherRoutesNeverMatch()
    {
        var rule = TestData.Rule(fromArea: Guid.NewGuid(), toArea: Guid.NewGuid());

        Assert.Null(PricingCalculator.SelectRule([rule], ServiceType.Standard, Guid.NewGuid(), Guid.NewGuid(), 2m, Now));
        Assert.Null(PricingCalculator.SelectRule([rule], ServiceType.Standard, null, null, 2m, Now));
    }

    [Fact]
    public void SelectRule_AmongEquallySpecificRules_HigherPriorityWins_ThenNewest()
    {
        var low = TestData.Rule(priority: 1);
        var high = TestData.Rule(priority: 5);
        var older = TestData.Rule(priority: 5, from: Now.AddDays(-60));

        Assert.Same(high, PricingCalculator.SelectRule([low, high], ServiceType.Standard, null, null, 2m, Now));
        Assert.Same(high, PricingCalculator.SelectRule([older, high], ServiceType.Standard, null, null, 2m, Now));
    }

    [Fact]
    public void SelectRule_SkipsInactiveExpiredAndFutureRules()
    {
        var inactive = TestData.Rule();
        inactive.Deactivate(Now.AddDays(-1));
        var expired = TestData.Rule(from: Now.AddDays(-30), to: Now.AddDays(-1));
        var future = TestData.Rule(from: Now.AddDays(1));
        var current = TestData.Rule(basePrice: 999m);

        var chosen = PricingCalculator.SelectRule([inactive, expired, future, current], ServiceType.Standard, null, null, 2m, Now);

        Assert.Same(current, chosen);
    }

    [Fact]
    public void SelectRule_ChangingAPrice_KeepsTheOldRuleForTheOldPeriodAndTheNewOneAfterwards()
    {
        var oldRule = TestData.Rule(basePrice: 150m, from: Now.AddDays(-30), to: Now);
        var newRule = TestData.Rule(basePrice: 180m, from: Now);

        Assert.Same(oldRule, PricingCalculator.SelectRule([oldRule, newRule], ServiceType.Standard, null, null, 2m, Now.AddDays(-1)));
        Assert.Same(newRule, PricingCalculator.SelectRule([oldRule, newRule], ServiceType.Standard, null, null, 2m, Now));
    }

    // ---- calculating the charge --------------------------------------------------------------------------------

    [Fact]
    public void Calculate_AddsBasePerKgChargeAboveTheBandStart_AndVat()
    {
        // Band 1–5 kg: NPR 150 + NPR 45 for each kg above 1 kg. 2.5 kg → 150 + 45 × 1.5 = 217.50. VAT 13% = 28.275 → 28.28.
        var price = PricingCalculator.Calculate(TestData.Rule(), 2.4m, 0m, 2.5m, 0m, 13m);

        Assert.Equal(150m, price.BaseCharge);
        Assert.Equal(67.5m, price.WeightCharge);
        Assert.Equal(217.5m, price.ShippingCharge);
        Assert.Equal(0m, price.CodFee);
        Assert.Equal(28.28m, price.VatAmount);
        Assert.Equal(245.78m, price.TotalCharge);
        Assert.Equal(2.4m, price.ActualWeightKg);
        Assert.Equal(2.5m, price.ChargeableWeightKg);
    }

    [Fact]
    public void Calculate_CodFeeIsAPercentageOfTheGoodsAmount_AndIsTaxed()
    {
        // 1.5% of NPR 4000 = 60. VAT 13% of (217.50 + 60) = 36.075 → 36.08. Total = 217.50 + 60 + 36.08.
        var price = PricingCalculator.Calculate(TestData.Rule(), 2.5m, 0m, 2.5m, 4000m, 13m);

        Assert.Equal(60m, price.CodFee);
        Assert.Equal(36.08m, price.VatAmount);
        Assert.Equal(313.58m, price.TotalCharge);
    }

    [Fact]
    public void Calculate_WithoutVat_TotalIsShippingPlusCodFee()
    {
        var price = PricingCalculator.Calculate(TestData.Rule(), 2.5m, 0m, 2.5m, 1000m, 0m);

        Assert.Equal(0m, price.VatAmount);
        Assert.Equal(217.5m + 15m, price.TotalCharge);
    }

    [Fact]
    public void Calculate_FlatRule_ChargesTheBaseOnly()
    {
        var price = PricingCalculator.Calculate(TestData.Rule(minKg: 0m, maxKg: 1m, basePrice: 150m, pricePerKg: 0m), 0.4m, 0m, 0.5m, 0m, 13m);

        Assert.Equal(150m, price.ShippingCharge);
        Assert.Equal(0m, price.WeightCharge);
    }

    [Fact]
    public void Calculate_BreakdownAlwaysAddsUp()
    {
        foreach (var weight in new[] { 1.5m, 2m, 3.5m, 4.5m, 5m })
        {
            var p = PricingCalculator.Calculate(TestData.Rule(), weight, 0m, weight, 2500m, 13m);

            Assert.Equal(p.BaseCharge + p.WeightCharge, p.ShippingCharge);
            Assert.Equal(p.ShippingCharge + p.CodFee + p.VatAmount, p.TotalCharge);
        }
    }

    [Fact]
    public void Calculate_RuleThatDoesNotCoverTheWeight_Throws() =>
        Assert.Throws<DomainException>(() => PricingCalculator.Calculate(TestData.Rule(), 8m, 0m, 8m, 0m, 13m));

    [Fact]
    public void Calculate_InvalidInputs_Throw()
    {
        Assert.Throws<DomainException>(() => PricingCalculator.Calculate(TestData.Rule(), 2m, 0m, 2m, -1m, 13m));
        Assert.Throws<DomainException>(() => PricingCalculator.Calculate(TestData.Rule(), 2m, 0m, 2m, 0m, 101m));
        Assert.Throws<DomainException>(() => PricingCalculator.Calculate(TestData.Rule(), 2m, 0m, 2m, 0m, -1m));
    }
}
