using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Delivery;
using NepalParcel.Application.Network;
using NepalParcel.Application.Parcels;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Payments;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Delivery;
using NepalParcel.Domain.Payments;

namespace NepalParcel.Application.Payments;

/// <summary>
/// Cash on delivery from the road to the accountant. An agent hands in all the cash they have collected as one settlement; the
/// accountant counts it. A count that matches accepts the settlement and settles every collection in it; a count that differs
/// makes it <em>disputed</em> (with a written explanation) until someone resolves it. Nothing is ever deleted or rewritten: the
/// expected total, the counted amount and the variance all stay on the record.
/// </summary>
internal sealed partial class SettlementService(
    ICodSettlementRepository settlements,
    ICodCollectionRepository cods,
    IDeliveryAgentRepository agents,
    IBranchRepository branches,
    IParcelRepository parcels,
    AgentContext agentContext,
    WorkplaceAccess access,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IValidator<CreateSettlementRequest> createValidator,
    IValidator<VerifySettlementRequest> verifyValidator,
    IValidator<ResolveSettlementRequest> resolveValidator,
    IValidator<SettlementQuery> queryValidator,
    ILogger<SettlementService> logger) : ISettlementService
{
    private const int ReferenceAttempts = 5;

    // ---- the agent ---------------------------------------------------------------------------------------------------------

    public async Task<SettlementResponse> SubmitAsync(CreateSettlementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await createValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var agent = await agentContext.RequireAgentAsync(cancellationToken).ConfigureAwait(false);
        var inHand = await cods.GetInHandForAgentAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        if (inHand.Count == 0)
        {
            throw new DomainException("You have no cash to hand in.");
        }

        var now = clock.GetUtcNow();
        var total = inHand.Sum(c => c.CollectedAmount);
        var settlement = CodSettlement.Create(await NewReferenceAsync(now, cancellationToken).ConfigureAwait(false), agent.BranchId, total, inHand.Count, agent.Id);
        settlement.Submit(now, request.Remarks);
        settlements.Add(settlement);
        foreach (var collection in inHand)
        {
            collection.AttachToSettlement(settlement.Id);
        }

        AddAudit(AuditActions.SettlementSubmitted, settlement.Id, now, new { settlement.ReferenceNumber, agentId = agent.Id, total, count = inHand.Count });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogSubmitted(logger, settlement.Id, agent.Id, inHand.Count);
        return (await ToResponsesAsync([settlement], withCollections: true, cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<AgentCashSummaryResponse> GetMyCashAsync(CancellationToken cancellationToken = default)
    {
        var agent = await agentContext.RequireAgentAsync(cancellationToken).ConfigureAwait(false);
        var rows = await cods.GetCodRowsAsync(null, agent.Id, cancellationToken).ConfigureAwait(false);
        var branchName = (await branches.GetByIdsAsync([agent.BranchId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(agent.BranchId)?.Name ?? string.Empty;
        var (recent, _) = await settlements.SearchAsync(new SettlementFilter(AgentId: agent.Id), 1, 5, cancellationToken).ConfigureAwait(false);
        return new AgentCashSummaryResponse(
            Position(agent, branchName, rows.Where(r => r.AgentId == agent.Id).ToList()),
            await ToResponsesAsync(recent, withCollections: false, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PagedResponse<SettlementResponse>> GetMineAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > PageQuery.MaxPageSize)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["Paging"] = [$"Page must be at least 1 and the page size between 1 and {PageQuery.MaxPageSize}."],
            });
        }

        var agent = await agentContext.RequireAgentAsync(cancellationToken).ConfigureAwait(false);
        var (items, total) = await settlements.SearchAsync(new SettlementFilter(AgentId: agent.Id), page, pageSize, cancellationToken).ConfigureAwait(false);
        return new PagedResponse<SettlementResponse>(
            await ToResponsesAsync(items, withCollections: false, cancellationToken).ConfigureAwait(false),
            page,
            pageSize,
            total);
    }

    // ---- the accountant ------------------------------------------------------------------------------------------------------

    public async Task<PagedResponse<SettlementResponse>> SearchAsync(SettlementQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await queryValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var branchId = await access.ScopeFinanceBranchAsync(query.BranchId, cancellationToken).ConfigureAwait(false);
        var filter = new SettlementFilter(
            string.IsNullOrWhiteSpace(query.Status) ? null : Enum.Parse<SettlementStatus>(query.Status, ignoreCase: true),
            query.AgentId,
            branchId,
            query.From is { } from ? NepalDay.StartUtc(from) : null,
            query.To is { } to ? NepalDay.StartUtc(to.AddDays(1)) : null);

        var (items, total) = await settlements.SearchAsync(filter, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return new PagedResponse<SettlementResponse>(
            await ToResponsesAsync(items, withCollections: false, cancellationToken).ConfigureAwait(false),
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<SettlementResponse> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var settlement = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        await access.ScopeFinanceBranchAsync(settlement.BranchId, cancellationToken).ConfigureAwait(false);
        return (await ToResponsesAsync([settlement], withCollections: true, cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<SettlementResponse> VerifyAsync(Guid id, VerifySettlementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await verifyValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var userId = currentUser.UserId ?? throw new UnauthorizedException("Authentication is required.");
        var settlement = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        await access.ScopeFinanceBranchAsync(settlement.BranchId, cancellationToken).ConfigureAwait(false);
        if (settlement.Status != SettlementStatus.Submitted)
        {
            throw new ConflictException($"This settlement is already '{settlement.Status}'.");
        }

        var collections = await cods.GetBySettlementAsync(settlement.Id, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        if (request.CountedAmount == settlement.TotalAmount)
        {
            settlement.Verify(now, userId, request.CountedAmount, request.Remarks);
            foreach (var collection in collections)
            {
                collection.MarkSettled();
            }

            AddAudit(AuditActions.SettlementVerified, settlement.Id, now, new { settlement.ReferenceNumber, total = settlement.TotalAmount, counted = request.CountedAmount });
            LogVerified(logger, settlement.Id, userId);
        }
        else
        {
            // A shortage or an excess is never absorbed silently: it needs an explanation and stays disputed until resolved.
            if (string.IsNullOrWhiteSpace(request.Remarks))
            {
                throw new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["Remarks"] = [string.Create(
                        CultureInfo.InvariantCulture,
                        $"The count (NPR {request.CountedAmount:0.00}) differs from the settlement (NPR {settlement.TotalAmount:0.00}). Explain the difference in the remarks.")],
                });
            }

            settlement.Dispute(request.Remarks, request.CountedAmount, userId);
            foreach (var collection in collections)
            {
                collection.MarkDisputed();
            }

            AddAudit(
                AuditActions.SettlementDisputed,
                settlement.Id,
                now,
                new { settlement.ReferenceNumber, total = settlement.TotalAmount, counted = request.CountedAmount, variance = settlement.Variance });
            LogDisputed(logger, settlement.Id, userId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await ToResponsesAsync([settlement], withCollections: true, cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<SettlementResponse> ResolveAsync(Guid id, ResolveSettlementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await resolveValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var userId = currentUser.UserId ?? throw new UnauthorizedException("Authentication is required.");
        var settlement = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        await access.ScopeFinanceBranchAsync(settlement.BranchId, cancellationToken).ConfigureAwait(false);
        if (settlement.Status != SettlementStatus.Disputed)
        {
            throw new ConflictException($"Only a disputed settlement can be resolved. This one is '{settlement.Status}'.");
        }

        var now = clock.GetUtcNow();
        settlement.Resolve(now, userId, request.Remarks);
        foreach (var collection in await cods.GetBySettlementAsync(settlement.Id, cancellationToken).ConfigureAwait(false))
        {
            collection.MarkSettled();
        }

        AddAudit(AuditActions.SettlementResolved, settlement.Id, now, new { settlement.ReferenceNumber, variance = settlement.Variance });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await ToResponsesAsync([settlement], withCollections: true, cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyList<AgentCashPositionRow>> GetAgentCashAsync(AgentCashQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var branchId = await access.ScopeFinanceBranchAsync(query.BranchId, cancellationToken).ConfigureAwait(false);
        var people = await agents.GetForReportAsync(branchId, query.AgentId, cancellationToken).ConfigureAwait(false);
        var rows = await cods.GetCodRowsAsync(branchId, query.AgentId, cancellationToken).ConfigureAwait(false);
        var names = (await branches.GetByIdsAsync(people.Select(a => a.BranchId).Distinct().ToList(), cancellationToken).ConfigureAwait(false))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Name);

        return people
            .Select(a => Position(a, names.GetValueOrDefault(a.BranchId, string.Empty), rows.Where(r => r.AgentId == a.Id).ToList()))
            .Where(p => p.CollectionCount > 0 || people.Single(a => a.Id == p.AgentId).IsActive)
            .OrderByDescending(p => p.InHand + p.InSettlement + p.Disputed)
            .ThenBy(p => p.FullName, StringComparer.Ordinal)
            .ToList();
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------------

    /// <summary>Where one agent's cash stands, from all the collections they have made.</summary>
    internal static AgentCashPositionRow Position(DeliveryAgent agent, string branchName, IReadOnlyList<CodRow> rows)
    {
        decimal Sum(Func<CodRow, bool> where) => rows.Where(where).Sum(r => r.CollectedAmount);

        return new AgentCashPositionRow(
            agent.Id,
            agent.FullName,
            branchName,
            rows.Sum(r => r.CollectedAmount),
            Sum(r => r.Status == CodCollectionStatus.Collected && r.SettlementId is null),
            Sum(r => r.Status == CodCollectionStatus.Collected && r.SettlementId is not null),
            Sum(r => r.Status == CodCollectionStatus.Disputed),
            Sum(r => r.Status == CodCollectionStatus.Settled),
            rows.Count,
            rows.Count == 0 ? null : rows.Max(r => r.CollectedAtUtc));
    }

    private async Task<CodSettlement> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await settlements.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("Settlement", id);

    private async Task<string> NewReferenceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        for (var attempt = 0; attempt < ReferenceAttempts; attempt++)
        {
            var suffix = string.Create(6, 0, static (span, _) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
                }
            });
            var reference = $"SET-{now.UtcDateTime:yyyyMMdd}-{suffix}";
            if (!await settlements.ReferenceExistsAsync(reference, cancellationToken).ConfigureAwait(false))
            {
                return reference;
            }
        }

        throw new ConflictException("A settlement reference could not be created. Please try again.");
    }

    private async Task<IReadOnlyList<SettlementResponse>> ToResponsesAsync(IReadOnlyList<CodSettlement> items, bool withCollections, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var branchNames = (await branches.GetByIdsAsync(items.Select(s => s.BranchId).Distinct().ToList(), cancellationToken).ConfigureAwait(false))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        var agentIds = items.Select(s => s.DeliveryAgentId).OfType<Guid>().Distinct().ToList();
        var agentNames = agentIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await agents.GetByIdsAsync(agentIds, cancellationToken).ConfigureAwait(false)).ToDictionary(kv => kv.Key, kv => kv.Value.FullName);

        var collectionsBySettlement = new Dictionary<Guid, IReadOnlyList<SettlementCollectionDto>>();
        if (withCollections)
        {
            foreach (var settlement in items)
            {
                var collections = await cods.GetBySettlementAsync(settlement.Id, cancellationToken).ConfigureAwait(false);
                var tracking = (await parcels.GetByIdsAsync(collections.Select(c => c.ParcelId).ToList(), cancellationToken).ConfigureAwait(false));
                collectionsBySettlement[settlement.Id] = collections
                    .OrderBy(c => c.CollectedAtUtc)
                    .Select(c => new SettlementCollectionDto(
                        c.ParcelId,
                        tracking.TryGetValue(c.ParcelId, out var parcel) ? parcel.TrackingNumber : string.Empty,
                        c.ExpectedAmount,
                        c.CollectedAmount,
                        c.CollectedAtUtc,
                        CashStatuses.For(c.Status, c.IsInSettlement)))
                    .ToList();
            }
        }

        return items
            .Select(s => new SettlementResponse(
                s.Id,
                s.ReferenceNumber,
                s.BranchId,
                branchNames.GetValueOrDefault(s.BranchId, string.Empty),
                s.DeliveryAgentId,
                s.DeliveryAgentId is { } agentId ? agentNames.GetValueOrDefault(agentId) : null,
                s.TotalAmount,
                s.CountedAmount,
                s.Variance,
                s.CollectionCount,
                s.Status.ToString(),
                s.CreatedAtUtc,
                s.SubmittedAtUtc,
                s.VerifiedAtUtc,
                s.Remarks,
                collectionsBySettlement.GetValueOrDefault(s.Id, [])))
            .ToList();
    }

    private void AddAudit(string action, Guid settlementId, DateTimeOffset now, object values) =>
        auditLogs.Add(AuditLog.Create(
            action,
            "CodSettlement",
            settlementId.ToString(),
            currentUser.UserId,
            now,
            newValues: JsonSerializer.Serialize(values),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));

    [LoggerMessage(EventId = 9511, Level = LogLevel.Information, Message = "Settlement {SettlementId}: agent {AgentId} handed in {Count} collection(s)")]
    private static partial void LogSubmitted(ILogger logger, Guid settlementId, Guid agentId, int count);

    [LoggerMessage(EventId = 9512, Level = LogLevel.Information, Message = "Settlement {SettlementId} verified by {UserId}")]
    private static partial void LogVerified(ILogger logger, Guid settlementId, Guid userId);

    [LoggerMessage(EventId = 9513, Level = LogLevel.Warning, Message = "Settlement {SettlementId} disputed by {UserId}: the count differs from the total")]
    private static partial void LogDisputed(ILogger logger, Guid settlementId, Guid userId);
}
