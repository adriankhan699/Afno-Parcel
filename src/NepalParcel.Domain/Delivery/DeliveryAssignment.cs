using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Delivery;

public enum AssignmentType
{
    Pickup = 1,
    Delivery = 2,
    Return = 3,
}

public enum AssignmentStatus
{
    Assigned = 1,
    Accepted = 2,
    InProgress = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,
}

/// <summary>A task giving a delivery agent responsibility for one leg of a parcel. Failed attempts keep their row.</summary>
public sealed class DeliveryAssignment : AuditableEntity
{
    private DeliveryAssignment()
    {
    }

    public Guid ParcelId { get; private set; }

    public Guid DeliveryAgentId { get; private set; }

    public AssignmentType Type { get; private set; }

    public AssignmentStatus Status { get; private set; }

    /// <summary>1 for the first attempt; incremented for each re-attempt of the same type.</summary>
    public int AttemptNumber { get; private set; }

    public DateTimeOffset AssignedAtUtc { get; private set; }

    public Guid? AssignedByUserId { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? FailureReason { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public bool IsOpen => Status is AssignmentStatus.Assigned or AssignmentStatus.Accepted or AssignmentStatus.InProgress;

    public static DeliveryAssignment Assign(
        Guid parcelId,
        Guid deliveryAgentId,
        AssignmentType type,
        DateTimeOffset assignedAtUtc,
        Guid? assignedByUserId = null,
        int attemptNumber = 1)
    {
        if (!Enum.IsDefined(type))
        {
            throw new DomainException($"'{type}' is not a valid assignment type.");
        }

        if (attemptNumber < 1)
        {
            throw new DomainException("Attempt number must be at least 1.");
        }

        return new DeliveryAssignment
        {
            ParcelId = Guard.NotEmpty(parcelId, "Parcel"),
            DeliveryAgentId = Guard.NotEmpty(deliveryAgentId, "Delivery agent"),
            Type = type,
            Status = AssignmentStatus.Assigned,
            AttemptNumber = attemptNumber,
            AssignedAtUtc = assignedAtUtc,
            AssignedByUserId = assignedByUserId,
        };
    }

    public void Accept()
    {
        Require(AssignmentStatus.Assigned);
        Status = AssignmentStatus.Accepted;
    }

    public void Start(DateTimeOffset atUtc)
    {
        if (Status is not (AssignmentStatus.Assigned or AssignmentStatus.Accepted))
        {
            throw new DomainException($"An assignment in status '{Status}' cannot be started.");
        }

        Status = AssignmentStatus.InProgress;
        StartedAtUtc = atUtc;
    }

    public void Complete(DateTimeOffset atUtc)
    {
        Require(AssignmentStatus.InProgress);
        Status = AssignmentStatus.Completed;
        CompletedAtUtc = atUtc;
    }

    public void Fail(DateTimeOffset atUtc, string reason)
    {
        Require(AssignmentStatus.InProgress);

        // Check the reason before changing anything, so a refused call leaves the assignment as it was.
        var text = Guard.NotBlank(reason, "Failure reason", 300);
        Status = AssignmentStatus.Failed;
        CompletedAtUtc = atUtc;
        FailureReason = text;
    }

    public void Cancel(DateTimeOffset atUtc)
    {
        if (!IsOpen)
        {
            throw new DomainException($"An assignment in status '{Status}' cannot be cancelled.");
        }

        Status = AssignmentStatus.Cancelled;
        CompletedAtUtc = atUtc;
    }

    private void Require(AssignmentStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainException($"Operation requires status '{expected}' but the assignment is '{Status}'.");
        }
    }
}
