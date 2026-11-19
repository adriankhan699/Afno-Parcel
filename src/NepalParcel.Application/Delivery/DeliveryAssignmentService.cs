using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Network;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Delivery;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.Application.Delivery;

/// <summary>
/// Branch managers (and super administrators) giving parcels to delivery agents and taking them back. A branch manager works
/// only with agents of their own branch and with parcels that are at, or heading to, their branch.
/// </summary>
internal sealed partial class DeliveryAssignmentService(
    IDeliveryAssignmentRepository assignments,
    IDeliveryAgentRepository agents,
    IDeliveryProofRepository proofs,
    ICodCollectionRepository cods,
    IParcelRepository parcels,
    IWarehouseRepository warehouses,
    IProofStorage storage,
    WorkplaceAccess access,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IValidator<AssignDeliveryRequest> assignValidator,
    IValidator<CancelAssignmentRequest> cancelValidator,
    IValidator<DeliveryAssignmentQuery> queryValidator,
    IValidator<ReadyParcelQuery> readyValidator,
    ILogger<DeliveryAssignmentService> logger) : IDeliveryAssignmentService
{
    public async Task<DeliveryAssignmentResponse> AssignAsync(AssignDeliveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await assignValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var agent = await agents.GetByIdAsync(request.DeliveryAgentId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Delivery agent", request.DeliveryAgentId);
        var parcel = await parcels.GetByIdAsync(request.ParcelId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Parcel", request.ParcelId);

        await EnsureMayAssignAsync(agent, parcel, cancellationToken).ConfigureAwait(false);

        if (!agent.IsActive)
        {
            throw new DomainException("This agent is switched off and cannot be given parcels.");
        }

        if (!agent.IsAvailable)
        {
            throw new DomainException("This agent is marked as not available. Choose another agent or ask them to switch availability on.");
        }

        if (!ParcelStatusWorkflow.CanTransition(parcel.Status, ParcelStatusCode.OutForDelivery))
        {
            throw new DomainException(
                $"A parcel that is '{ParcelStatusCatalog.Get(parcel.Status).Name}' is not ready for delivery. " +
                "It can be given to an agent once it has been shipped, is in transit, has arrived at a warehouse, or a delivery attempt has failed.");
        }

        if (await assignments.GetOpenForParcelAsync(parcel.Id, AssignmentType.Delivery, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new ConflictException("This parcel is already assigned to a delivery agent. Take it back first to give it to someone else.");
        }

        var attempt = await assignments.GetLastAttemptNumberAsync(parcel.Id, AssignmentType.Delivery, cancellationToken).ConfigureAwait(false) + 1;
        var now = clock.GetUtcNow();
        var assignment = DeliveryAssignment.Assign(parcel.Id, agent.Id, AssignmentType.Delivery, now, currentUser.UserId, attempt);
        assignments.Add(assignment);
        AddAudit(AuditActions.DeliveryAssigned, assignment.Id, now, new { parcelId = parcel.Id, agentId = agent.Id, attempt });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogAssigned(logger, assignment.Id, parcel.Id, agent.Id, currentUser.UserId);
        return (await ToResponsesAsync([assignment], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<DeliveryAssignmentResponse> CancelAsync(Guid id, CancelAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await cancelValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var assignment = await assignments.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Delivery assignment", id);
        var agent = await agents.GetByIdAsync(assignment.DeliveryAgentId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Delivery agent", assignment.DeliveryAgentId);
        await access.EnsureCanManageBranchAsync(agent.BranchId, cancellationToken).ConfigureAwait(false);

        if (assignment.Status == AssignmentStatus.InProgress)
        {
            throw new DomainException("The agent has already taken this parcel out for delivery. It cannot be taken back until the delivery is finished or has failed.");
        }

        var now = clock.GetUtcNow();
        assignment.Cancel(now);
        AddAudit(AuditActions.DeliveryAssignmentCancelled, assignment.Id, now, new { reason = request.Reason.Trim(), parcelId = assignment.ParcelId });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await ToResponsesAsync([assignment], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<PagedResponse<DeliveryAssignmentResponse>> GetPageAsync(DeliveryAssignmentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await queryValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var branchId = await ScopeBranchAsync(query.BranchId, cancellationToken).ConfigureAwait(false);
        IReadOnlyCollection<AssignmentStatus>? statuses = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            statuses = string.Equals(query.Status, DeliveryRules.OpenStatus, StringComparison.OrdinalIgnoreCase)
                ? DeliveryRules.OpenStatuses
                : [Enum.Parse<AssignmentStatus>(query.Status, ignoreCase: true)];
        }

        var filter = new AssignmentFilter(
            query.DeliveryAgentId,
            query.ParcelId,
            branchId,
            statuses,
            query.From is { } from ? NepalDay.StartUtc(from) : null,
            query.To is { } to ? NepalDay.StartUtc(to.AddDays(1)) : null);
        var (items, total) = await assignments.SearchAsync(filter, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return new PagedResponse<DeliveryAssignmentResponse>(
            await ToResponsesAsync(items, cancellationToken).ConfigureAwait(false),
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<PagedResponse<ReadyParcelResponse>> GetReadyAsync(ReadyParcelQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await readyValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var branchId = await ScopeBranchAsync(query.BranchId, cancellationToken).ConfigureAwait(false);
        var (rows, total) = await assignments
            .GetReadyParcelsAsync(new ReadyFilter(branchId, query.Search?.Trim()), query.Page, query.PageSize, cancellationToken)
            .ConfigureAwait(false);

        var now = clock.GetUtcNow();
        return new PagedResponse<ReadyParcelResponse>(
            rows.Select(r => new ReadyParcelResponse(
                r.ParcelId,
                r.TrackingNumber,
                ((ParcelStatusCode)r.StatusId).ToString(),
                ParcelMappings.StatusName(r.StatusId),
                r.Location,
                r.ReceiverName,
                r.ReceiverDistrict,
                r.ReceiverMunicipality,
                r.ServiceType.ToString(),
                r.CodAmount,
                r.FailedAttempts,
                r.SinceUtc,
                decimal.Round((decimal)Math.Max(0, (now - r.SinceUtc).TotalHours), 1, MidpointRounding.AwayFromZero))).ToList(),
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<ProofFile> GetProofFileAsync(Guid proofId, CancellationToken cancellationToken = default)
    {
        var proof = await proofs.GetByIdAsync(proofId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Proof of delivery", proofId);
        if (string.IsNullOrEmpty(proof.StorageKey))
        {
            throw new NotFoundException("Proof of delivery", proofId);
        }

        var stream = await storage.OpenAsync(proof.StorageKey, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Proof of delivery", proofId);
        return new ProofFile(stream, ProofImage.ContentTypeFor(proof.StorageKey));
    }

    /// <summary>A branch manager may use only agents of their own branch, on parcels at or heading to their branch.</summary>
    private async Task EnsureMayAssignAsync(DeliveryAgent agent, Parcel parcel, CancellationToken cancellationToken)
    {
        if (access.IsSuperAdmin)
        {
            return;
        }

        await access.EnsureCanManageBranchAsync(agent.BranchId, cancellationToken).ConfigureAwait(false);

        var branchId = agent.BranchId;
        var atMyBranch = parcel.CurrentBranchId == branchId || parcel.DestinationBranchId == branchId;
        if (!atMyBranch && parcel.CurrentWarehouseId is { } warehouseId)
        {
            var warehouse = await warehouses.GetByIdAsync(warehouseId, cancellationToken).ConfigureAwait(false);
            atMyBranch = warehouse?.BranchId == branchId;
        }

        if (!atMyBranch)
        {
            throw new ForbiddenException("This parcel is not at, or heading to, your branch, so you cannot assign it.");
        }
    }

    private async Task<Guid?> ScopeBranchAsync(Guid? requested, CancellationToken cancellationToken)
    {
        if (access.IsSuperAdmin)
        {
            return requested;
        }

        var mine = await access.GetMyAssignmentAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ForbiddenException("You are not assigned to a branch yet. Ask a super administrator to assign you.");
        if (requested is { } branch && branch != mine.BranchId)
        {
            throw new ForbiddenException("A branch manager can only see their own branch.");
        }

        return mine.BranchId;
    }

    private async Task<IReadOnlyList<DeliveryAssignmentResponse>> ToResponsesAsync(IReadOnlyList<DeliveryAssignment> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var parcelMap = await parcels.GetByIdsAsync(items.Select(a => a.ParcelId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        var agentMap = await agents.GetByIdsAsync(items.Select(a => a.DeliveryAgentId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        var proofMap = await proofs.GetByAssignmentsAsync(items.Select(a => a.Id).ToList(), cancellationToken).ConfigureAwait(false);
        var codMap = await cods.GetByParcelsAsync(parcelMap.Keys.ToList(), cancellationToken).ConfigureAwait(false);

        return items
            .Select(a =>
            {
                var parcel = parcelMap[a.ParcelId];
                return new DeliveryAssignmentResponse(
                    a.Id,
                    parcel.Id,
                    parcel.TrackingNumber,
                    a.DeliveryAgentId,
                    agentMap.TryGetValue(a.DeliveryAgentId, out var agent) ? agent.FullName : "Unknown agent",
                    a.Type.ToString(),
                    a.Status.ToString(),
                    a.AttemptNumber,
                    a.AssignedAtUtc,
                    a.StartedAtUtc,
                    a.CompletedAtUtc,
                    a.FailureReason,
                    parcel.Status.ToString(),
                    parcel.ReceiverName,
                    parcel.ReceiverAddress.District,
                    parcel.CodAmount,
                    codMap.TryGetValue(parcel.Id, out var cod) ? cod.CollectedAmount : null,
                    proofMap.TryGetValue(a.Id, out var list) ? list.Select(p => p.ToResponse()).ToList() : []);
            })
            .ToList();
    }

    private void AddAudit(string action, Guid assignmentId, DateTimeOffset now, object values) =>
        auditLogs.Add(AuditLog.Create(
            action,
            "DeliveryAssignment",
            assignmentId.ToString(),
            currentUser.UserId,
            now,
            newValues: JsonSerializer.Serialize(values),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));

    [LoggerMessage(EventId = 9411, Level = LogLevel.Information, Message = "Delivery assignment {AssignmentId}: parcel {ParcelId} given to agent {AgentId} by {ActorId}")]
    private static partial void LogAssigned(ILogger logger, Guid assignmentId, Guid parcelId, Guid agentId, Guid? actorId);
}
