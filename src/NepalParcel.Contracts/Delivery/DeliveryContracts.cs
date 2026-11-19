namespace NepalParcel.Contracts.Delivery;

// ---- delivery agents (managed by SuperAdmin and branch managers) ---------------------------------------------------

/// <summary>
/// Creates a delivery agent: a login account with the DeliveryAgent role, an agent profile, and an assignment to the branch
/// they ride from. The password is chosen by the person creating the account and is never shown again.
/// </summary>
public sealed record CreateDeliveryAgentRequest(
    string FullName,
    string Email,
    string PhoneNumber,
    string Password,
    Guid BranchId,
    string? VehicleType = null,
    string? VehicleNumber = null,
    string? LicenseNumber = null);

/// <summary>Changes an agent's contact and vehicle details. The name, login and branch are not changed here.</summary>
public sealed record UpdateDeliveryAgentRequest(
    string PhoneNumber,
    string? VehicleType = null,
    string? VehicleNumber = null,
    string? LicenseNumber = null);

/// <summary>Whether an agent can take new assignments.</summary>
public sealed record SetAvailabilityRequest(bool IsAvailable);

/// <summary>A delivery agent as shown to managers.</summary>
public sealed record DeliveryAgentResponse(
    Guid Id,
    Guid UserId,
    string FullName,
    string? Email,
    string PhoneNumber,
    Guid BranchId,
    string BranchName,
    string? VehicleType,
    string? VehicleNumber,
    string? LicenseNumber,
    bool IsActive,
    bool IsAvailable,
    int OpenAssignments,
    DateTimeOffset CreatedAtUtc);

/// <summary>Filters for the agent list.</summary>
public sealed record DeliveryAgentQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    /// <summary>Part of the name, phone number or vehicle number.</summary>
    public string? Search { get; init; }

    public Guid? BranchId { get; init; }

    public bool? IsActive { get; init; }

    public bool? IsAvailable { get; init; }
}

// ---- assigning parcels ---------------------------------------------------------------------------------------------

/// <summary>Gives a parcel to a delivery agent for the final leg.</summary>
public sealed record AssignDeliveryRequest(Guid ParcelId, Guid DeliveryAgentId);

/// <summary>Takes a parcel back from an agent who has not started yet. A reason is required.</summary>
public sealed record CancelAssignmentRequest(string Reason);

/// <summary>A proof of delivery as listed (the file itself is fetched separately by staff).</summary>
public sealed record DeliveryProofResponse(
    Guid Id,
    string Type,
    string? RecipientName,
    bool OtpVerified,
    bool HasFile,
    DateTimeOffset CapturedAtUtc);

/// <summary>An assignment as shown to managers.</summary>
public sealed record DeliveryAssignmentResponse(
    Guid Id,
    Guid ParcelId,
    string TrackingNumber,
    Guid DeliveryAgentId,
    string AgentName,
    string Type,
    string Status,
    int AttemptNumber,
    DateTimeOffset AssignedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureReason,
    string ParcelStatus,
    string ReceiverName,
    string ReceiverDistrict,
    decimal CodAmount,
    decimal? CodCollectedAmount,
    IReadOnlyList<DeliveryProofResponse> Proofs);

/// <summary>Filters for the assignment list.</summary>
public sealed record DeliveryAssignmentQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    public Guid? DeliveryAgentId { get; init; }

    public Guid? ParcelId { get; init; }

    public Guid? BranchId { get; init; }

    /// <summary>Assigned, Accepted, InProgress, Completed, Failed or Cancelled; "Open" means the first three.</summary>
    public string? Status { get; init; }

    /// <summary>First day (inclusive) by assignment date, as a Nepal-time calendar date.</summary>
    public DateOnly? From { get; init; }

    /// <summary>Last day (inclusive), as a Nepal-time calendar date.</summary>
    public DateOnly? To { get; init; }
}

/// <summary>A parcel that is ready for a delivery agent and has none yet.</summary>
public sealed record ReadyParcelResponse(
    Guid ParcelId,
    string TrackingNumber,
    string Status,
    string StatusName,
    string Location,
    string ReceiverName,
    string ReceiverDistrict,
    string ReceiverMunicipality,
    string ServiceType,
    decimal CodAmount,
    int FailedAttempts,
    DateTimeOffset WaitingSinceUtc,
    decimal WaitingHours);

/// <summary>Filters for the ready-parcels list.</summary>
public sealed record ReadyParcelQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    /// <summary>Only parcels at or heading to this branch. A branch manager is always limited to their own branch.</summary>
    public Guid? BranchId { get; init; }

    /// <summary>Part of the tracking number, receiver name or district.</summary>
    public string? Search { get; init; }
}

// ---- the agent's own screens ---------------------------------------------------------------------------------------

/// <summary>The signed-in agent's profile and how their day is going.</summary>
public sealed record AgentSummaryResponse(
    Guid AgentId,
    string FullName,
    string BranchName,
    bool IsAvailable,
    int OpenAssignments,
    int DeliveredToday,
    int FailedToday,
    decimal CodCollectedToday,
    decimal CodCollectedUnsettled);

/// <summary>
/// One of the agent's assignments with what they need to do the job. The receiver's phone number and full address are sent
/// only while the assignment is open; afterwards only the name and area remain.
/// </summary>
public sealed record AgentAssignmentResponse(
    Guid Id,
    Guid ParcelId,
    string TrackingNumber,
    string Status,
    int AttemptNumber,
    DateTimeOffset AssignedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureReason,
    string ParcelStatus,
    string ReceiverName,
    string? ReceiverPhone,
    string? ReceiverAddress,
    string? ReceiverInstructions,
    string ReceiverArea,
    string ServiceType,
    string Description,
    int Quantity,
    decimal WeightKg,
    decimal CodAmount,
    bool CodCollected,
    decimal? CodCollectedAmount,
    bool HasProof,
    IReadOnlyList<DeliveryProofResponse> Proofs,
    bool CanStart,
    bool CanFail,
    bool CanComplete,
    bool CanReturnToWarehouse);

/// <summary>Filters for the agent's own list.</summary>
public sealed record AgentAssignmentQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    /// <summary>"open" (default) or "history".</summary>
    public string? Scope { get; init; }
}

/// <summary>Why a delivery attempt failed. Required.</summary>
public sealed record MarkDeliveryFailedRequest(string Reason, string? Remarks = null);

/// <summary>Cash taken from the receiver. Must equal the amount due exactly.</summary>
public sealed record CollectCodRequest(decimal CollectedAmount);

/// <summary>The cash collection recorded for a parcel.</summary>
public sealed record CodCollectionResponse(
    Guid Id,
    Guid ParcelId,
    decimal ExpectedAmount,
    decimal CollectedAmount,
    DateTimeOffset CollectedAtUtc);

/// <summary>Said to the agent after a one-time delivery code has been sent to the receiver's phone.</summary>
public sealed record DeliveryCodeSentResponse(string Message, int ValidForMinutes);

/// <summary>The code the receiver read out, as proof that they are the receiver.</summary>
public sealed record VerifyDeliveryCodeRequest(string Code, string? RecipientName = null);

/// <summary>Finishes the delivery. Needs a proof, and the cash if the parcel is cash on delivery.</summary>
public sealed record CompleteDeliveryRequest(string? Remarks = null);

/// <summary>Returns a parcel whose delivery failed to a warehouse.</summary>
public sealed record ReturnToWarehouseRequest(Guid WarehouseId, string? Remarks = null);

// ---- performance ---------------------------------------------------------------------------------------------------

/// <summary>The period and branch a performance report covers. Dates are Nepal-time calendar days.</summary>
public sealed record AgentPerformanceQuery
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public Guid? BranchId { get; init; }

    public Guid? DeliveryAgentId { get; init; }
}

/// <summary>
/// How one agent did over the period, counting assignments given in it. Success rate is delivered divided by delivered plus
/// failed. Average delivery time runs from assignment to completion, in hours.
/// </summary>
public sealed record AgentPerformanceRow(
    Guid DeliveryAgentId,
    string FullName,
    string BranchName,
    bool IsActive,
    int Assigned,
    int Delivered,
    int Failed,
    int Cancelled,
    int InProgress,
    decimal? SuccessRatePercent,
    decimal? FirstAttemptSuccessPercent,
    decimal? AverageDeliveryHours,
    int CodCollections,
    decimal CodCollected);

/// <summary>The whole report: one row per agent and the totals.</summary>
public sealed record AgentPerformanceReport(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<AgentPerformanceRow> Agents,
    AgentPerformanceRow Totals);
