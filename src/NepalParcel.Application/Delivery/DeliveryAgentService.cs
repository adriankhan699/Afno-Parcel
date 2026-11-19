using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Network;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Delivery;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Branches;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Identity;

namespace NepalParcel.Application.Delivery;

/// <summary>
/// Creating and managing delivery agents, and reporting on how they do. A super administrator works on every branch; a branch
/// manager only on the agents of the branch they are assigned to.
/// </summary>
internal sealed partial class DeliveryAgentService(
    IDeliveryAgentRepository agents,
    IDeliveryAssignmentRepository deliveries,
    ICodCollectionRepository cods,
    IBranchRepository branches,
    IStaffAssignmentRepository staffAssignments,
    IIdentityService identity,
    WorkplaceAccess access,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IValidator<CreateDeliveryAgentRequest> createValidator,
    IValidator<UpdateDeliveryAgentRequest> updateValidator,
    IValidator<DeliveryAgentQuery> queryValidator,
    IValidator<AgentPerformanceQuery> reportValidator,
    ILogger<DeliveryAgentService> logger) : IDeliveryAgentService
{
    public async Task<DeliveryAgentResponse> CreateAsync(CreateDeliveryAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await createValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var branch = await branches.GetByIdAsync(request.BranchId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Branch", request.BranchId);
        if (!branch.IsActive)
        {
            throw new DomainException("Agents can only be added to a branch that is active.");
        }

        await access.EnsureCanManageBranchAsync(branch.Id, cancellationToken).ConfigureAwait(false);

        var created = await identity
            .CreateUserAsync(request.FullName.Trim(), request.Email.Trim(), request.PhoneNumber, request.Password, Roles.DeliveryAgent, cancellationToken)
            .ConfigureAwait(false);
        if (!created.Succeeded)
        {
            throw created.Failures.ToException();
        }

        var user = created.User!;
        var now = clock.GetUtcNow();
        var agent = DeliveryAgent.Create(user.Id, branch.Id, request.FullName, request.PhoneNumber, request.VehicleType, request.VehicleNumber, request.LicenseNumber);
        agents.Add(agent);

        // The agent works at their branch (and so at its warehouses), like any other member of staff.
        staffAssignments.Add(StaffAssignment.Create(user.Id, branch.Id, null, now, currentUser.UserId));
        AddAudit(AuditActions.AgentCreated, agent.Id, now, new { agent.BranchId, userId = user.Id });

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Do not leave a login with no agent profile behind.
            unitOfWork.DiscardPendingChanges();
            await identity.DeleteUserAsync(user.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        LogCreated(logger, agent.Id, branch.Id, currentUser.UserId);
        return (await ToResponsesAsync([agent], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<DeliveryAgentResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var agent = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        await EnsureMayViewAsync(agent, cancellationToken).ConfigureAwait(false);
        return (await ToResponsesAsync([agent], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<PagedResponse<DeliveryAgentResponse>> GetPageAsync(DeliveryAgentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await queryValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var branchId = await ScopeBranchAsync(query.BranchId, cancellationToken).ConfigureAwait(false);
        var (items, total) = await agents
            .SearchAsync(new AgentFilter(query.Search?.Trim(), branchId, query.IsActive, query.IsAvailable), query.Page, query.PageSize, cancellationToken)
            .ConfigureAwait(false);
        return new PagedResponse<DeliveryAgentResponse>(
            await ToResponsesAsync(items, cancellationToken).ConfigureAwait(false),
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<DeliveryAgentResponse> UpdateAsync(Guid id, UpdateDeliveryAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await updateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var agent = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        await access.EnsureCanManageBranchAsync(agent.BranchId, cancellationToken).ConfigureAwait(false);

        agent.UpdateDetails(request.PhoneNumber, request.VehicleType, request.VehicleNumber, request.LicenseNumber);
        AddAudit(AuditActions.AgentUpdated, agent.Id, clock.GetUtcNow(), new { agent.BranchId });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await ToResponsesAsync([agent], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<DeliveryAgentResponse> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var agent = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        await access.EnsureCanManageBranchAsync(agent.BranchId, cancellationToken).ConfigureAwait(false);
        if (agent.IsActive)
        {
            throw new ConflictException("This agent is already active.");
        }

        var branch = await branches.GetByIdAsync(agent.BranchId, cancellationToken).ConfigureAwait(false);
        if (branch is null || !branch.IsActive)
        {
            throw new DomainException("The agent's branch is not active. Activate the branch first.");
        }

        agent.Activate();
        await identity.SetActiveAsync(agent.UserId, true, cancellationToken).ConfigureAwait(false);
        AddAudit(AuditActions.AgentActivated, agent.Id, clock.GetUtcNow(), new { agent.BranchId });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await ToResponsesAsync([agent], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<DeliveryAgentResponse> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var agent = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        await access.EnsureCanManageBranchAsync(agent.BranchId, cancellationToken).ConfigureAwait(false);
        if (!agent.IsActive)
        {
            throw new ConflictException("This agent is already inactive.");
        }

        var open = (await agents.CountOpenAssignmentsAsync([agent.Id], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(agent.Id);
        if (open > 0)
        {
            throw new DomainException($"This agent still has {open} open assignment(s). Take them back or let the agent finish them first.");
        }

        agent.Deactivate();
        await identity.SetActiveAsync(agent.UserId, false, cancellationToken).ConfigureAwait(false);
        AddAudit(AuditActions.AgentDeactivated, agent.Id, clock.GetUtcNow(), new { agent.BranchId });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogDeactivated(logger, agent.Id, currentUser.UserId);
        return (await ToResponsesAsync([agent], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<DeliveryAgentResponse> SetAvailabilityAsync(Guid id, SetAvailabilityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        await access.EnsureCanManageBranchAsync(agent.BranchId, cancellationToken).ConfigureAwait(false);

        agent.SetAvailability(request.IsAvailable);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await ToResponsesAsync([agent], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<AgentPerformanceReport> GetPerformanceAsync(AgentPerformanceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await reportValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var branchId = await ScopeBranchAsync(query.BranchId, cancellationToken).ConfigureAwait(false);
        var today = NepalDay.Today(clock.GetUtcNow());
        var to = query.To ?? today;
        var from = query.From ?? to.AddDays(-29);
        var fromUtc = NepalDay.StartUtc(from);
        var beforeUtc = NepalDay.StartUtc(to.AddDays(1));

        var people = await agents.GetForReportAsync(branchId, query.DeliveryAgentId, cancellationToken).ConfigureAwait(false);
        var stats = await deliveries.GetStatsAsync(fromUtc, beforeUtc, branchId, query.DeliveryAgentId, cancellationToken).ConfigureAwait(false);
        var collections = await cods.GetStatsAsync(fromUtc, beforeUtc, branchId, query.DeliveryAgentId, cancellationToken).ConfigureAwait(false);
        var branchNames = await BranchNamesAsync(people.Select(a => a.BranchId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);

        var rows = people
            .Select(a => (Agent: a, Stats: stats.Where(s => s.AgentId == a.Id).ToList(), Cod: collections.Where(c => c.AgentId == a.Id).ToList()))
            .Where(x => x.Agent.IsActive || x.Stats.Count > 0 || x.Cod.Count > 0)
            .Select(x => Row(x.Agent.Id, x.Agent.FullName, branchNames.GetValueOrDefault(x.Agent.BranchId, string.Empty), x.Agent.IsActive, x.Stats, x.Cod))
            .ToList();

        var totals = Row(Guid.Empty, "All agents", string.Empty, true, stats.Where(s => rows.Any(r => r.DeliveryAgentId == s.AgentId)).ToList(), collections.Where(c => rows.Any(r => r.DeliveryAgentId == c.AgentId)).ToList());
        return new AgentPerformanceReport(from, to, rows, totals);
    }

    private static AgentPerformanceRow Row(Guid id, string name, string branch, bool active, IReadOnlyList<AssignmentStat> stats, IReadOnlyList<CodStat> cod)
    {
        var delivered = stats.Count(s => s.Status == AssignmentStatus.Completed);
        var failed = stats.Count(s => s.Status == AssignmentStatus.Failed);
        var cancelled = stats.Count(s => s.Status == AssignmentStatus.Cancelled);
        var open = stats.Count(s => s.Status is AssignmentStatus.Assigned or AssignmentStatus.Accepted or AssignmentStatus.InProgress);

        decimal? success = delivered + failed == 0 ? null : Percent(delivered, delivered + failed);

        var firstAttempts = stats.Where(s => s.AttemptNumber == 1 && s.Status is AssignmentStatus.Completed or AssignmentStatus.Failed).ToList();
        decimal? firstAttempt = firstAttempts.Count == 0 ? null : Percent(firstAttempts.Count(s => s.Status == AssignmentStatus.Completed), firstAttempts.Count);

        var durations = stats
            .Where(s => s.Status == AssignmentStatus.Completed && s.CompletedAtUtc is not null)
            .Select(s => (s.CompletedAtUtc!.Value - s.AssignedAtUtc).TotalHours)
            .ToList();
        decimal? average = durations.Count == 0 ? null : decimal.Round((decimal)Math.Max(0, durations.Average()), 1, MidpointRounding.AwayFromZero);

        return new AgentPerformanceRow(id, name, branch, active, stats.Count, delivered, failed, cancelled, open, success, firstAttempt, average, cod.Count, cod.Sum(c => c.CollectedAmount));
    }

    private static decimal Percent(int part, int whole) => decimal.Round(part * 100m / whole, 1, MidpointRounding.AwayFromZero);

    /// <summary>A branch manager only ever sees their own branch; a super administrator sees what they ask for.</summary>
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

    private async Task EnsureMayViewAsync(DeliveryAgent agent, CancellationToken cancellationToken)
    {
        if (access.IsSuperAdmin)
        {
            return;
        }

        var mine = await access.GetMyAssignmentAsync(cancellationToken).ConfigureAwait(false);
        if (mine is null || mine.BranchId != agent.BranchId)
        {
            throw new ForbiddenException("A branch manager can only see agents of their own branch.");
        }
    }

    private async Task<DeliveryAgent> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await agents.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("Delivery agent", id);

    private async Task<IReadOnlyDictionary<Guid, string>> BranchNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? new Dictionary<Guid, string>()
            : (await branches.GetByIdsAsync(ids, cancellationToken).ConfigureAwait(false)).ToDictionary(kv => kv.Key, kv => kv.Value.Name);

    private async Task<IReadOnlyList<DeliveryAgentResponse>> ToResponsesAsync(IReadOnlyList<DeliveryAgent> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var branchNames = await BranchNamesAsync(items.Select(a => a.BranchId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        var open = await agents.CountOpenAssignmentsAsync(items.Select(a => a.Id).ToList(), cancellationToken).ConfigureAwait(false);
        var userIds = items.Select(a => a.UserId).ToList();
        var (users, _) = await identity
            .ListStaffAsync(new StaffUserFilter(OnlyUserIds: userIds), 1, userIds.Count, cancellationToken)
            .ConfigureAwait(false);
        var emails = users.ToDictionary(u => u.Id, u => u.Email);

        return items
            .Select(a => new DeliveryAgentResponse(
                a.Id,
                a.UserId,
                a.FullName,
                emails.GetValueOrDefault(a.UserId),
                a.PhoneNumber,
                a.BranchId,
                branchNames.GetValueOrDefault(a.BranchId, string.Empty),
                a.VehicleType,
                a.VehicleNumber,
                a.LicenseNumber,
                a.IsActive,
                a.IsAvailable,
                open.GetValueOrDefault(a.Id),
                a.CreatedAtUtc))
            .ToList();
    }

    private void AddAudit(string action, Guid agentId, DateTimeOffset now, object values) =>
        auditLogs.Add(AuditLog.Create(
            action,
            "DeliveryAgent",
            agentId.ToString(),
            currentUser.UserId,
            now,
            newValues: JsonSerializer.Serialize(values),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));

    [LoggerMessage(EventId = 9401, Level = LogLevel.Information, Message = "Delivery agent {AgentId} created at branch {BranchId} by {ActorId}")]
    private static partial void LogCreated(ILogger logger, Guid agentId, Guid branchId, Guid? actorId);

    [LoggerMessage(EventId = 9402, Level = LogLevel.Information, Message = "Delivery agent {AgentId} deactivated by {ActorId}")]
    private static partial void LogDeactivated(ILogger logger, Guid agentId, Guid? actorId);
}
