using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Payments;

public enum TransactionType
{
    Charge = 1,
    Refund = 2,
}

public enum TransactionStatus
{
    Pending = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>
/// One attempt against a payment provider (eSewa, Khalti, bank...). Append-only audit trail of money movement;
/// do not store card numbers, secrets or full gateway payloads here.
/// </summary>
public sealed class PaymentTransaction : Entity
{
    private PaymentTransaction()
    {
        Provider = string.Empty;
    }

    public Guid PaymentId { get; private set; }

    public TransactionType Type { get; private set; }

    public TransactionStatus Status { get; private set; }

    public string Provider { get; private set; }

    /// <summary>The provider's own reference/transaction id, unique per provider.</summary>
    public string? ProviderReference { get; private set; }

    /// <summary>Amount in NPR.</summary>
    public decimal Amount { get; private set; }

    /// <summary>
    /// Our own reference for this attempt, unique across all transactions. It is what we ask the provider about and what a
    /// callback must quote; it is never the provider's reference.
    /// </summary>
    public string MerchantReference { get; private set; } = string.Empty;

    public string Currency { get; private set; } = Money.NprCurrencyCode;

    /// <summary>For an online attempt: when it is given up if the provider never confirms it.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    /// <summary>For money taken by a person (cash at the office, cash on delivery): who recorded it.</summary>
    public Guid? RecordedByUserId { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static PaymentTransaction Create(
        Guid paymentId,
        TransactionType type,
        string provider,
        decimal amount,
        DateTimeOffset createdAtUtc,
        string? providerReference = null,
        string? merchantReference = null,
        DateTimeOffset? expiresAtUtc = null,
        Guid? recordedByUserId = null)
    {
        if (!Enum.IsDefined(type))
        {
            throw new DomainException($"'{type}' is not a valid transaction type.");
        }

        return new PaymentTransaction
        {
            PaymentId = Guard.NotEmpty(paymentId, "Payment"),
            Type = type,
            Status = TransactionStatus.Pending,
            Provider = Guard.NotBlank(provider, "Provider", 50),
            ProviderReference = Guard.Optional(providerReference, "Provider reference", 100),
            Amount = Guard.Positive(amount, "Transaction amount", 2),
            MerchantReference = Guard.NotBlank(merchantReference ?? MerchantReferences.New(), "Merchant reference", 40),
            Currency = Money.NprCurrencyCode,
            ExpiresAtUtc = expiresAtUtc,
            RecordedByUserId = recordedByUserId,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>Whether an online attempt has run out of time without being confirmed.</summary>
    public bool IsExpired(DateTimeOffset nowUtc) => ExpiresAtUtc is { } expires && nowUtc >= expires;

    public void Succeed(DateTimeOffset atUtc, string? providerReference = null)
    {
        EnsurePending();
        Status = TransactionStatus.Succeeded;
        CompletedAtUtc = atUtc;
        ProviderReference = Guard.Optional(providerReference, "Provider reference", 100) ?? ProviderReference;
    }

    public void Fail(DateTimeOffset atUtc, string reason)
    {
        EnsurePending();
        Status = TransactionStatus.Failed;
        CompletedAtUtc = atUtc;
        FailureReason = Guard.NotBlank(reason, "Failure reason", 500);
    }

    private void EnsurePending()
    {
        if (Status != TransactionStatus.Pending)
        {
            throw new DomainException($"A transaction in status '{Status}' can no longer change.");
        }
    }
}
