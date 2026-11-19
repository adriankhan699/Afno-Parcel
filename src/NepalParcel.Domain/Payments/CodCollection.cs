using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Payments;

public enum CodCollectionStatus
{
    Collected = 1,
    Settled = 2,
    Disputed = 3,
}

/// <summary>Cash collected by a delivery agent from the receiver of a cash-on-delivery parcel.</summary>
public sealed class CodCollection : AuditableEntity
{
    private CodCollection()
    {
    }

    public Guid ParcelId { get; private set; }

    public Guid DeliveryAgentId { get; private set; }

    /// <summary>Amount the parcel required, in NPR.</summary>
    public decimal ExpectedAmount { get; private set; }

    /// <summary>Amount actually collected, in NPR.</summary>
    public decimal CollectedAmount { get; private set; }

    public decimal Variance => CollectedAmount - ExpectedAmount;

    public DateTimeOffset CollectedAtUtc { get; private set; }

    public CodCollectionStatus Status { get; private set; }

    public Guid? SettlementId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static CodCollection Record(Guid parcelId, Guid deliveryAgentId, decimal expectedAmount, decimal collectedAmount, DateTimeOffset collectedAtUtc) =>
        new()
        {
            ParcelId = Guard.NotEmpty(parcelId, "Parcel"),
            DeliveryAgentId = Guard.NotEmpty(deliveryAgentId, "Delivery agent"),
            ExpectedAmount = Guard.NonNegative(expectedAmount, "Expected amount"),
            CollectedAmount = Guard.NonNegative(collectedAmount, "Collected amount"),
            CollectedAtUtc = collectedAtUtc,
            Status = CodCollectionStatus.Collected,
        };

    /// <summary>
    /// Puts the cash into a settlement (the agent handing it in). It stays <em>collected</em> until the accountant has counted
    /// the cash and accepted it.
    /// </summary>
    public void AttachToSettlement(Guid settlementId)
    {
        if (Status != CodCollectionStatus.Collected || SettlementId is not null)
        {
            throw new DomainException("Only collected cash that has not been handed in can be added to a settlement.");
        }

        SettlementId = Guard.NotEmpty(settlementId, "Settlement");
    }

    /// <summary>The accountant counted the cash and accepted it (also used when a disputed count is resolved).</summary>
    public void MarkSettled()
    {
        if (SettlementId is null || Status is not (CodCollectionStatus.Collected or CodCollectionStatus.Disputed))
        {
            throw new DomainException("Only cash that is part of a settlement can be marked as settled.");
        }

        Status = CodCollectionStatus.Settled;
    }

    /// <summary>The count did not match the settlement.</summary>
    public void MarkDisputed()
    {
        if (Status == CodCollectionStatus.Settled)
        {
            throw new DomainException("A settled COD collection cannot be disputed.");
        }

        if (SettlementId is null)
        {
            throw new DomainException("Only cash that is part of a settlement can be disputed.");
        }

        Status = CodCollectionStatus.Disputed;
    }

    /// <summary>Handed in and waiting for the accountant's count.</summary>
    public bool IsInSettlement => SettlementId is not null && Status == CodCollectionStatus.Collected;

    /// <summary>With the agent: collected and not handed in.</summary>
    public bool IsInHand => SettlementId is null && Status == CodCollectionStatus.Collected;
}
