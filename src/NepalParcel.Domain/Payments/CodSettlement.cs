using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Payments;

public enum SettlementStatus
{
    Draft = 1,
    Submitted = 2,
    Verified = 3,
    Disputed = 4,
}

/// <summary>A batch hand-over of collected COD cash from an agent to the branch accountant.</summary>
public sealed class CodSettlement : AuditableEntity
{
    private CodSettlement()
    {
        ReferenceNumber = string.Empty;
    }

    public string ReferenceNumber { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid? DeliveryAgentId { get; private set; }

    /// <summary>Total cash being settled, in NPR.</summary>
    public decimal TotalAmount { get; private set; }

    public int CollectionCount { get; private set; }

    public SettlementStatus Status { get; private set; }

    public DateTimeOffset? SubmittedAtUtc { get; private set; }

    public DateTimeOffset? VerifiedAtUtc { get; private set; }

    public Guid? VerifiedByUserId { get; private set; }

    public string? Remarks { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static CodSettlement Create(string referenceNumber, Guid branchId, decimal totalAmount, int collectionCount, Guid? deliveryAgentId = null)
    {
        if (collectionCount < 1)
        {
            throw new DomainException("A settlement must include at least one collection.");
        }

        return new CodSettlement
        {
            ReferenceNumber = Guard.NotBlank(referenceNumber, "Reference number", 30).ToUpperInvariant(),
            BranchId = Guard.NotEmpty(branchId, "Branch"),
            DeliveryAgentId = deliveryAgentId,
            TotalAmount = Guard.NonNegative(totalAmount, "Total amount"),
            CollectionCount = collectionCount,
            Status = SettlementStatus.Draft,
        };
    }

    public void Submit(DateTimeOffset atUtc)
    {
        Require(SettlementStatus.Draft);
        Status = SettlementStatus.Submitted;
        SubmittedAtUtc = atUtc;
    }

    public void Verify(DateTimeOffset atUtc, Guid verifiedByUserId)
    {
        Require(SettlementStatus.Submitted);
        Status = SettlementStatus.Verified;
        VerifiedAtUtc = atUtc;
        VerifiedByUserId = Guard.NotEmpty(verifiedByUserId, "Verifier");
    }

    public void Dispute(string remarks)
    {
        Require(SettlementStatus.Submitted);
        Status = SettlementStatus.Disputed;
        Remarks = Guard.NotBlank(remarks, "Remarks", 500);
    }

    private void Require(SettlementStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainException($"Operation requires status '{expected}' but the settlement is '{Status}'.");
        }
    }
}
