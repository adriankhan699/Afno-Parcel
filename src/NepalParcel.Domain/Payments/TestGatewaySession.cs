using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Payments;

public enum TestSessionStatus
{
    Pending = 1,
    Paid = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>
/// The books of the <b>simulated</b> payment provider used in test mode: what a real provider would remember about each
/// attempt. The server never trusts a browser redirect; it asks the gateway, and in test mode the gateway reads this table.
/// A real integration replaces the test gateways and this table is no longer used.
/// </summary>
public sealed class TestGatewaySession : Entity
{
    private TestGatewaySession()
    {
        Provider = string.Empty;
        MerchantReference = string.Empty;
        Currency = Money.NprCurrencyCode;
    }

    public string Provider { get; private init; }

    public string MerchantReference { get; private init; }

    public decimal Amount { get; private init; }

    public string Currency { get; private init; }

    public TestSessionStatus Status { get; private set; }

    /// <summary>What the simulated provider calls the payment once it has been paid.</summary>
    public string? ProviderTransactionId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private init; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static TestGatewaySession Create(string provider, string merchantReference, decimal amount, DateTimeOffset createdAtUtc) =>
        new()
        {
            Provider = Guard.NotBlank(provider, "Provider", 50),
            MerchantReference = Guard.NotBlank(merchantReference, "Merchant reference", 40),
            Amount = Guard.Positive(amount, "Amount", 2),
            Status = TestSessionStatus.Pending,
            CreatedAtUtc = createdAtUtc,
        };

    /// <summary>The tester pressed a button on the simulated provider page. Only a pending session can be completed, once.</summary>
    public void Complete(TestSessionStatus outcome, DateTimeOffset atUtc, string? providerTransactionId)
    {
        if (outcome == TestSessionStatus.Pending)
        {
            throw new DomainException("A session cannot be completed as pending.");
        }

        if (Status != TestSessionStatus.Pending)
        {
            throw new DomainException($"This test payment is already {Status}.");
        }

        if (outcome == TestSessionStatus.Paid && string.IsNullOrWhiteSpace(providerTransactionId))
        {
            throw new DomainException("A paid session needs a provider transaction id.");
        }

        Status = outcome;
        CompletedAtUtc = atUtc;
        ProviderTransactionId = outcome == TestSessionStatus.Paid ? providerTransactionId : null;
    }
}
