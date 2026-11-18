using System.Globalization;

namespace NepalParcel.Domain.Common;

/// <summary>
/// An immutable monetary amount. The platform operates in Nepalese Rupees (NPR); the currency is
/// stored explicitly so the model stays correct if other currencies are ever introduced.
/// </summary>
public sealed record Money
{
    public const string NprCurrencyCode = "NPR";
    public const int DecimalPlaces = 2;

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; private init; }

    public string Currency { get; private init; }

    public static Money Zero { get; } = new(0m, NprCurrencyCode);

    /// <summary>Creates a non-negative NPR amount rounded to paisa (2 decimals, banker's rounding avoided).</summary>
    public static Money Npr(decimal amount)
    {
        if (amount < 0)
        {
            throw new DomainException("Money amount cannot be negative.");
        }

        return new Money(decimal.Round(amount, DecimalPlaces, MidpointRounding.AwayFromZero), NprCurrencyCode);
    }

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        if (other.Amount > Amount)
        {
            throw new DomainException("Resulting money amount cannot be negative.");
        }

        return new Money(Amount - other.Amount, Currency);
    }

    public Money Multiply(decimal factor)
    {
        if (factor < 0)
        {
            throw new DomainException("Multiplication factor cannot be negative.");
        }

        return new Money(decimal.Round(Amount * factor, DecimalPlaces, MidpointRounding.AwayFromZero), Currency);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Currency} {Amount:N2}");

    private void EnsureSameCurrency(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new DomainException($"Cannot combine {Currency} with {other.Currency}.");
        }
    }
}
