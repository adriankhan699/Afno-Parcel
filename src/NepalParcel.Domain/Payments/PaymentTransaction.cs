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

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static PaymentTransaction Create(
        Guid paymentId,
        TransactionType type,
        string provider,
        decimal amount,
        DateTimeOffset createdAtUtc,
        string? providerReference = null)
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
            CreatedAtUtc = createdAtUtc,
        };
    }

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
