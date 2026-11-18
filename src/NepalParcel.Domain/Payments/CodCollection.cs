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

    public void AttachToSettlement(Guid settlementId)
    {
        if (Status != CodCollectionStatus.Collected || SettlementId is not null)
        {
            throw new DomainException("Only an unsettled, collected COD amount can be added to a settlement.");
        }

        SettlementId = Guard.NotEmpty(settlementId, "Settlement");
        Status = CodCollectionStatus.Settled;
    }

    public void MarkDisputed()
    {
        if (Status == CodCollectionStatus.Settled)
        {
            throw new DomainException("A settled COD collection cannot be disputed.");
        }

        Status = CodCollectionStatus.Disputed;
    }
}
