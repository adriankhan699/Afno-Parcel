using NepalParcel.Domain.Common;

namespace NepalParcel.UnitTests.Domain;

public sealed class MoneyTests
{
    [Fact]
    public void Npr_RoundsToTwoDecimals_AwayFromZero()
    {
        var money = Money.Npr(100.005m);

        Assert.Equal(100.01m, money.Amount);
        Assert.Equal("NPR", money.Currency);
    }

    [Fact]
    public void Npr_NegativeAmount_Throws()
    {
        Assert.Throws<DomainException>(() => Money.Npr(-1m));
    }

    [Fact]
    public void Add_And_Subtract_ReturnNewValues()
    {
        var total = Money.Npr(150m).Add(Money.Npr(49.50m));

        Assert.Equal(199.50m, total.Amount);
        Assert.Equal(99.50m, total.Subtract(Money.Npr(100m)).Amount);
    }

    [Fact]
    public void Subtract_BelowZero_Throws()
    {
        Assert.Throws<DomainException>(() => Money.Npr(10m).Subtract(Money.Npr(20m)));
    }

    [Fact]
    public void Multiply_RoundsResult()
    {
        Assert.Equal(33.33m, Money.Npr(100m).Multiply(1m / 3m).Amount);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(Money.Npr(250m), Money.Npr(250.00m));
    }

    [Fact]
    public void ToString_UsesCurrencyCodeAndGrouping()
    {
        Assert.Equal("NPR 1,250.50", Money.Npr(1250.5m).ToString());
    }
}
