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

    /// <summary>What the accountant counted when the cash was handed over. Null until then.</summary>
    public decimal? CountedAmount { get; private set; }

    /// <summary>Counted minus expected: negative is a shortage, positive an excess.</summary>
    public decimal? Variance => CountedAmount is { } counted ? counted - TotalAmount : null;

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

    public void Submit(DateTimeOffset atUtc, string? remarks = null)
    {
        Require(SettlementStatus.Draft);
        var text = Guard.Optional(remarks, "Remarks", 500);
        Status = SettlementStatus.Submitted;
        SubmittedAtUtc = atUtc;
        Remarks = text;
    }

    /// <summary>The count matches the total: the settlement is accepted.</summary>
    public void Verify(DateTimeOffset atUtc, Guid verifiedByUserId, decimal countedAmount, string? remarks = null)
    {
        Require(SettlementStatus.Submitted);
        var counted = Guard.NonNegative(countedAmount, "Counted amount");
        if (counted != TotalAmount)
        {
            throw new DomainException("The counted amount does not match the settlement total, so it cannot be verified. Dispute it instead.");
        }

        var by = Guard.NotEmpty(verifiedByUserId, "Verifier");
        Status = SettlementStatus.Verified;
        CountedAmount = counted;
        VerifiedAtUtc = atUtc;
        VerifiedByUserId = by;
        Remarks = Guard.Optional(remarks, "Remarks", 500) ?? Remarks;
    }

    /// <summary>The count differs from the total: a shortage or an excess that someone has to explain.</summary>
    public void Dispute(string remarks, decimal countedAmount, Guid? disputedByUserId = null)
    {
        Require(SettlementStatus.Submitted);
        var text = Guard.NotBlank(remarks, "Remarks", 500);
        var counted = Guard.NonNegative(countedAmount, "Counted amount");
        Status = SettlementStatus.Disputed;
        CountedAmount = counted;
        VerifiedByUserId = disputedByUserId;
        Remarks = text;
    }

    /// <summary>A disputed settlement is looked into and accepted. The count and the variance stay on the record.</summary>
    public void Resolve(DateTimeOffset atUtc, Guid resolvedByUserId, string remarks)
    {
        Require(SettlementStatus.Disputed);
        var text = Guard.NotBlank(remarks, "Remarks", 500);
        var by = Guard.NotEmpty(resolvedByUserId, "Resolver");
        Status = SettlementStatus.Verified;
        VerifiedAtUtc = atUtc;
        VerifiedByUserId = by;
        Remarks = text;
    }

    private void Require(SettlementStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainException($"Operation requires status '{expected}' but the settlement is '{Status}'.");
        }
    }
}
