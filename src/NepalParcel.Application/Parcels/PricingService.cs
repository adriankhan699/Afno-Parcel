using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Mapping;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Parcels;
using NepalParcel.Domain.Pricing;

namespace NepalParcel.Application.Parcels;

internal sealed partial class PricingService(
    PriceEngine engine,
    IBranchRepository branches,
    IPricingRepository pricing,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IValidator<QuoteRequest> quoteValidator,
    IValidator<CreatePricingRuleRequest> ruleValidator,
    IValidator<PageQuery> pageValidator,
    ILogger<PricingService> logger) : IPricingService
{
    public async Task<QuoteResponse> QuoteAsync(QuoteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await quoteValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var origin = await branches.GetByIdAsync(request.OriginBranchId, cancellationToken).ConfigureAwait(false);
        if (origin is null || !origin.IsActive)
        {
            throw new NotFoundException("Branch", request.OriginBranchId);
        }

        var serviceType = Enum.Parse<ServiceType>(request.ServiceType, ignoreCase: true);
        var result = await engine
            .CalculateAsync(
                origin,
                request.Destination.ToDomain(),
                serviceType,
                request.WeightKg,
                request.LengthCm,
                request.WidthCm,
                request.HeightCm,
                request.CodAmount ?? 0m,
                clock.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);

        return new QuoteResponse(
            serviceType.ToString(),
            result.OriginArea?.Name,
            result.DestinationArea.Name,
            result.EstimatedDays,
            result.EstimatedDeliveryAtUtc,
            result.Rule.Name,
            Money.NprCurrencyCode,
            result.Breakdown.ToDto());
    }

    public async Task<IReadOnlyList<ServiceAreaResponse>> GetServiceAreasAsync(CancellationToken cancellationToken = default)
    {
        var areas = await pricing.GetServiceAreasAsync(cancellationToken).ConfigureAwait(false);
        return areas
            .OrderBy(a => (int)a.Province)
            .ThenBy(a => a.District, StringComparer.Ordinal)
            .ThenBy(a => a.Municipality ?? string.Empty, StringComparer.Ordinal)
            .Select(a => a.ToResponse())
            .ToList();
    }

    public async Task<PagedResponse<PricingRuleResponse>> GetRulesAsync(PageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await pageValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var (rules, total) = await pricing.GetRulesPageAsync(query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        var names = await AreaNamesAsync(rules, cancellationToken).ConfigureAwait(false);

        return new PagedResponse<PricingRuleResponse>(rules.Select(r => r.ToResponse(names)).ToList(), query.Page, query.PageSize, total);
    }

    public async Task<PricingRuleResponse> CreateRuleAsync(CreatePricingRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ruleValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        foreach (var areaId in new[] { request.FromServiceAreaId, request.ToServiceAreaId }.OfType<Guid>())
        {
            if (!await pricing.ServiceAreaExistsAsync(areaId, cancellationToken).ConfigureAwait(false))
            {
                throw new NotFoundException("Service area", areaId);
            }
        }

        var now = clock.GetUtcNow();
        var rule = PricingRule.Create(
            request.Name,
            Enum.Parse<ServiceType>(request.ServiceType, ignoreCase: true),
            request.MinWeightKg,
            request.MaxWeightKg,
            request.BasePrice,
            request.PricePerKg,
            request.CodFeePercent,
            request.EffectiveFromUtc ?? now,
            request.FromServiceAreaId,
            request.ToServiceAreaId,
            request.EffectiveToUtc,
            request.Priority);

        pricing.AddRule(rule);
        AddAudit(AuditActions.PricingRuleCreated, rule.Id, now, new { rule.Name, serviceType = rule.ServiceType.ToString(), rule.BasePrice, rule.PricePerKg });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogRuleCreated(logger, rule.Id);
        var names = await AreaNamesAsync([rule], cancellationToken).ConfigureAwait(false);
        return rule.ToResponse(names);
    }

    public async Task DeactivateRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rule = await pricing.GetRuleByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Pricing rule", id);

        if (!rule.IsActive)
        {
            throw new ConflictException("This pricing rule is already inactive.");
        }

        var now = clock.GetUtcNow();
        rule.Deactivate(now);
        AddAudit(AuditActions.PricingRuleDeactivated, rule.Id, now, null);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogRuleDeactivated(logger, rule.Id);
    }

    private Task<IReadOnlyDictionary<Guid, string>> AreaNamesAsync(IEnumerable<PricingRule> rules, CancellationToken cancellationToken) =>
        pricing.GetServiceAreaNamesAsync(
            rules.SelectMany(r => new[] { r.FromServiceAreaId, r.ToServiceAreaId }).OfType<Guid>().Distinct().ToList(),
            cancellationToken);

    private void AddAudit(string action, Guid entityId, DateTimeOffset now, object? newValues) =>
        auditLogs.Add(AuditLog.Create(
            action,
            "PricingRule",
            entityId.ToString(),
            currentUser.UserId,
            now,
            newValues: newValues is null ? null : JsonSerializer.Serialize(newValues),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Pricing rule {RuleId} created")]
    private static partial void LogRuleCreated(ILogger logger, Guid ruleId);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Pricing rule {RuleId} deactivated")]
    private static partial void LogRuleDeactivated(ILogger logger, Guid ruleId);
}
