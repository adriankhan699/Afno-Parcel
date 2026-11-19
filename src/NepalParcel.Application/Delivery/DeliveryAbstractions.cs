using System.ComponentModel.DataAnnotations;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Delivery;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Application.Delivery;

/// <summary>Delivery settings (configuration section <c>Delivery</c>). Not secrets.</summary>
public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";

    /// <summary>Where signature and photo proofs are kept. A private folder on the server, never served as static files.</summary>
    [Required]
    [StringLength(260)]
    public string ProofStoragePath { get; set; } = "storage/proofs";

    /// <summary>Largest signature or photo accepted, in kilobytes.</summary>
    [Range(50, 20_000)]
    public int MaxProofSizeKb { get; set; } = 6_000;

    /// <summary>How many proofs may be recorded for one delivery.</summary>
    [Range(1, 20)]
    public int MaxProofsPerAssignment { get; set; } = 5;
}

public sealed record AgentFilter(string? Search = null, Guid? BranchId = null, bool? IsActive = null, bool? IsAvailable = null);

/// <param name="Statuses">When set, only assignments in one of these statuses.</param>
/// <param name="AgentBranchId">Only assignments of agents who ride from this branch.</param>
/// <param name="FromUtc">Inclusive start of the assignment date range.</param>
/// <param name="BeforeUtc">Exclusive end of the assignment date range.</param>
public sealed record AssignmentFilter(
    Guid? AgentId = null,
    Guid? ParcelId = null,
    Guid? AgentBranchId = null,
    IReadOnlyCollection<AssignmentStatus>? Statuses = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? BeforeUtc = null);

/// <summary>A parcel that can be given to a delivery agent.</summary>
public sealed record ReadyParcelRow(
    Guid ParcelId,
    string TrackingNumber,
    int StatusId,
    string Location,
    string ReceiverName,
    string ReceiverDistrict,
    string ReceiverMunicipality,
    ServiceType ServiceType,
    decimal CodAmount,
    int FailedAttempts,
    DateTimeOffset SinceUtc);

public sealed record ReadyFilter(Guid? BranchId = null, string? Search = null);

/// <summary>What the performance report needs from one delivery assignment.</summary>
public sealed record AssignmentStat(
    Guid AgentId,
    AssignmentStatus Status,
    int AttemptNumber,
    DateTimeOffset AssignedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>What the performance report needs from one cash collection.</summary>
public sealed record CodStat(Guid AgentId, decimal CollectedAmount);

public interface IDeliveryAgentRepository
{
    void Add(DeliveryAgent agent);

    /// <summary>Tracked, so it can be changed.</summary>
    Task<DeliveryAgent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The agent profile of a login account (tracked), or null when the account is not an agent.</summary>
    Task<DeliveryAgent?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, DeliveryAgent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<DeliveryAgent> Items, int TotalCount)> SearchAsync(AgentFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Agents for the performance report, optionally of one branch or just one agent.</summary>
    Task<IReadOnlyList<DeliveryAgent>> GetForReportAsync(Guid? branchId, Guid? agentId, CancellationToken cancellationToken = default);

    /// <summary>How many open (assigned, accepted or in progress) delivery assignments each agent has.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountOpenAssignmentsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken cancellationToken = default);
}

public interface IDeliveryAssignmentRepository
{
    void Add(DeliveryAssignment assignment);

    /// <summary>Tracked, so it can be changed.</summary>
    Task<DeliveryAssignment?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The parcel's open assignment of that type (tracked), or null.</summary>
    Task<DeliveryAssignment?> GetOpenForParcelAsync(Guid parcelId, AssignmentType type, CancellationToken cancellationToken = default);

    /// <summary>The highest attempt number used so far for the parcel and type (0 when there is none).</summary>
    Task<int> GetLastAttemptNumberAsync(Guid parcelId, AssignmentType type, CancellationToken cancellationToken = default);

    /// <summary>Whether the parcel has a later delivery attempt than <paramref name="attemptNumber"/> (open or finished).</summary>
    Task<bool> HasLaterAttemptAsync(Guid parcelId, int attemptNumber, CancellationToken cancellationToken = default);

    /// <summary>Newest first, delivery assignments only.</summary>
    Task<(IReadOnlyList<DeliveryAssignment> Items, int TotalCount)> SearchAsync(AssignmentFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// One agent's delivery assignments, newest first. "Open" means still to do (assigned, accepted, in progress) plus failed
    /// attempts whose parcel is waiting to be taken back to a warehouse; "history" is everything else.
    /// </summary>
    Task<(IReadOnlyList<DeliveryAssignment> Items, int TotalCount)> GetForAgentAsync(Guid agentId, bool open, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssignmentStat>> GetStatsAsync(DateTimeOffset fromUtc, DateTimeOffset beforeUtc, Guid? branchId, Guid? agentId, CancellationToken cancellationToken = default);

    /// <summary>How many deliveries the agent completed and failed since <paramref name="sinceUtc"/>.</summary>
    Task<(int Delivered, int Failed)> CountFinishedSinceAsync(Guid agentId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<ReadyParcelRow> Items, int TotalCount)> GetReadyParcelsAsync(ReadyFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}

public interface IDeliveryProofRepository
{
    void Add(DeliveryProof proof);

    Task<DeliveryProof?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeliveryProof>> GetByAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<DeliveryProof>>> GetByAssignmentsAsync(IReadOnlyCollection<Guid> assignmentIds, CancellationToken cancellationToken = default);

    Task<int> CountForAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default);
}

public interface ICodCollectionRepository
{
    void Add(CodCollection collection);

    Task<CodCollection?> GetByParcelAsync(Guid parcelId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, CodCollection>> GetByParcelsAsync(IReadOnlyCollection<Guid> parcelIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodStat>> GetStatsAsync(DateTimeOffset fromUtc, DateTimeOffset beforeUtc, Guid? branchId, Guid? agentId, CancellationToken cancellationToken = default);

    /// <summary>The agent's cash that has been collected and not yet handed in (tracked, so it can join a settlement).</summary>
    Task<IReadOnlyList<CodCollection>> GetInHandForAgentAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>The collections that belong to a settlement (tracked, so the count can settle or dispute them).</summary>
    Task<IReadOnlyList<CodCollection>> GetBySettlementAsync(Guid settlementId, CancellationToken cancellationToken = default);

    /// <summary>Every collection (optionally of one branch's agents or one agent) with just what the cash position needs.</summary>
    Task<IReadOnlyList<NepalParcel.Application.Payments.CodRow>> GetCodRowsAsync(Guid? branchId, Guid? agentId, CancellationToken cancellationToken = default);

    /// <summary>Cash the agent has collected that has not been handed in (settled) yet.</summary>
    Task<decimal> SumUnsettledAsync(Guid agentId, CancellationToken cancellationToken = default);

    Task<decimal> SumCollectedSinceAsync(Guid agentId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);
}

public interface IDeliveryOtpRepository
{
    void Add(DeliveryOtp otp);

    /// <summary>The newest code issued for the assignment (tracked so attempts can be recorded), or null.</summary>
    Task<DeliveryOtp?> GetLatestAsync(Guid assignmentId, CancellationToken cancellationToken = default);

    Task<int> CountSinceAsync(Guid assignmentId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);
}

/// <summary>Private storage for proof images (implemented in Infrastructure). Files are never reachable by a public address.</summary>
public interface IProofStorage
{
    /// <summary>Stores the bytes under a new random key and returns the key.</summary>
    Task<string> SaveAsync(ReadOnlyMemory<byte> content, string extension, CancellationToken cancellationToken = default);

    /// <summary>Opens the file, or returns null when it does not exist.</summary>
    Task<Stream?> OpenAsync(string key, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public interface IDeliveryAgentService
{
    Task<DeliveryAgentResponse> CreateAsync(CreateDeliveryAgentRequest request, CancellationToken cancellationToken = default);

    Task<DeliveryAgentResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResponse<DeliveryAgentResponse>> GetPageAsync(DeliveryAgentQuery query, CancellationToken cancellationToken = default);

    Task<DeliveryAgentResponse> UpdateAsync(Guid id, UpdateDeliveryAgentRequest request, CancellationToken cancellationToken = default);

    Task<DeliveryAgentResponse> ActivateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<DeliveryAgentResponse> DeactivateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<DeliveryAgentResponse> SetAvailabilityAsync(Guid id, SetAvailabilityRequest request, CancellationToken cancellationToken = default);

    Task<AgentPerformanceReport> GetPerformanceAsync(AgentPerformanceQuery query, CancellationToken cancellationToken = default);
}

public interface IDeliveryAssignmentService
{
    Task<DeliveryAssignmentResponse> AssignAsync(AssignDeliveryRequest request, CancellationToken cancellationToken = default);

    Task<DeliveryAssignmentResponse> CancelAsync(Guid id, CancelAssignmentRequest request, CancellationToken cancellationToken = default);

    Task<PagedResponse<DeliveryAssignmentResponse>> GetPageAsync(DeliveryAssignmentQuery query, CancellationToken cancellationToken = default);

    Task<PagedResponse<ReadyParcelResponse>> GetReadyAsync(ReadyParcelQuery query, CancellationToken cancellationToken = default);

    /// <summary>The proof image for staff to look at, or null when the proof has no file.</summary>
    Task<ProofFile> GetProofFileAsync(Guid proofId, CancellationToken cancellationToken = default);
}

/// <summary>An open proof image and its media type. The caller disposes the stream.</summary>
public sealed record ProofFile(Stream Content, string ContentType);

public interface IAgentWorkService
{
    Task<AgentSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken = default);

    Task<AgentSummaryResponse> SetAvailabilityAsync(SetAvailabilityRequest request, CancellationToken cancellationToken = default);

    Task<PagedResponse<AgentAssignmentResponse>> GetAssignmentsAsync(AgentAssignmentQuery query, CancellationToken cancellationToken = default);

    Task<AgentAssignmentResponse> GetAssignmentAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AgentAssignmentResponse> AcceptAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AgentAssignmentResponse> StartAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AgentAssignmentResponse> FailAsync(Guid id, MarkDeliveryFailedRequest request, CancellationToken cancellationToken = default);

    Task<DeliveryCodeSentResponse> SendDeliveryCodeAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AgentAssignmentResponse> VerifyDeliveryCodeAsync(Guid id, VerifyDeliveryCodeRequest request, CancellationToken cancellationToken = default);

    /// <param name="type">Signature or Photo.</param>
    Task<AgentAssignmentResponse> RecordFileProofAsync(Guid id, string type, string? recipientName, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    Task<CodCollectionResponse> CollectCodAsync(Guid id, CollectCodRequest request, CancellationToken cancellationToken = default);

    Task<AgentAssignmentResponse> CompleteAsync(Guid id, CompleteDeliveryRequest request, CancellationToken cancellationToken = default);

    Task<AgentAssignmentResponse> ReturnToWarehouseAsync(Guid id, ReturnToWarehouseRequest request, CancellationToken cancellationToken = default);
}
