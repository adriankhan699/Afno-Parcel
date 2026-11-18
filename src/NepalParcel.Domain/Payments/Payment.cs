using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Payments;

public enum PaymentMethod
{
    Cash = 1,
    Esewa = 2,
    Khalti = 3,
    ConnectIps = 4,
    BankTransfer = 5,
    CashOnDelivery = 6,
}

public enum PaymentStatus
{
    Pending = 1,
    Paid = 2,
    Failed = 3,
    Refunded = 4,
    PartiallyRefunded = 5,
}

/// <summary>A payment owed or made for a parcel's delivery charge (or COD). Gateway attempts live in <see cref="PaymentTransaction"/>.</summary>
public sealed class Payment : AuditableEntity
{
    private Payment()
    {
        Currency = Money.NprCurrencyCode;
    }

    public Guid ParcelId { get; private set; }

    public Guid? PayerCustomerId { get; private set; }

    /// <summary>Amount in NPR.</summary>
    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public PaymentMethod Method { get; private set; }

    public PaymentStatus Status { get; private set; }

    public DateTimeOffset? PaidAtUtc { get; private set; }

    public string? Reference { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static Payment Create(Guid parcelId, decimal amount, PaymentMethod method, Guid? payerCustomerId = null)
    {
        if (!Enum.IsDefined(method))
        {
            throw new DomainException($"'{method}' is not a valid payment method.");
        }

        return new Payment
        {
            ParcelId = Guard.NotEmpty(parcelId, "Parcel"),
            PayerCustomerId = payerCustomerId,
            Amount = Guard.Positive(amount, "Payment amount", 2),
            Method = method,
            Status = PaymentStatus.Pending,
        };
    }

    public void MarkPaid(DateTimeOffset atUtc, string? reference = null)
    {
        if (Status != PaymentStatus.Pending)
        {
            throw new DomainException($"A payment in status '{Status}' cannot be marked as paid.");
        }

        Status = PaymentStatus.Paid;
        PaidAtUtc = atUtc;
        Reference = Guard.Optional(reference, "Reference", 100);
    }

    public void MarkFailed()
    {
        if (Status != PaymentStatus.Pending)
        {
            throw new DomainException($"A payment in status '{Status}' cannot be marked as failed.");
        }

        Status = PaymentStatus.Failed;
    }

    public void MarkRefunded(bool partial)
    {
        if (Status is not (PaymentStatus.Paid or PaymentStatus.PartiallyRefunded))
        {
            throw new DomainException("Only a paid payment can be refunded.");
        }

        Status = partial ? PaymentStatus.PartiallyRefunded : PaymentStatus.Refunded;
    }
}
