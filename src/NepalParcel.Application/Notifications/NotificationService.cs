using System.Text.Json;
using FluentValidation;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Privacy;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Application.Delivery;
using NepalParcel.Application.Parcels;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Notifications;
using NepalParcel.Domain.Auditing;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Notifications;

namespace NepalParcel.Application.Notifications;

internal static class NotificationMappings
{
    public static NotificationMessageResponse ToResponse(this NotificationRow row)
    {
        var m = row.Message;
        return new NotificationMessageResponse(
            m.Id,
            m.Channel.ToString(),
            m.Audience.ToString(),
            m.TemplateKey,
            PrivacyMasking.MaskPhone(m.Recipient),
            m.Status.ToString(),
            m.DeliveryStatus.ToString(),
            m.Provider,
            m.ProviderMessageId,
            m.ParcelId,
            row.TrackingNumber,
            m.AttemptCount,
            m.MaxAttempts,
            m.CreatedAtUtc,
            m.NextAttemptAtUtc,
            m.SubmittedAtUtc,
            m.SentAtUtc,
            m.DeliveredAtUtc,
            m.ReadAtUtc,
            m.FailedAtUtc,
            m.SkipReason,
            m.LastError,
            m.DeliveryError);
    }

    public static NotificationAttemptResponse ToResponse(this NotificationLog log) =>
        new(log.Id, log.Status.ToString(), log.Provider, log.ProviderMessageId, log.AttemptNumber, log.Error, log.CreatedAtUtc, log.OccurredAtUtc);

    public static NotificationTemplateResponse ToResponse(this NotificationTemplate t) =>
        new(t.Id, t.Key, t.Channel.ToString(), t.Language, t.Body, t.ProviderTemplateName, TemplateRenderer.Parameters(t.Body), t.IsActive, t.Body.Length, t.UpdatedAtUtc);

    public static OptOutResponse ToResponse(this NotificationOptOut o) =>
        new(o.Id, PrivacyMasking.MaskPhone(o.PhoneNumber), o.Channel.ToString(), o.Source.ToString(), o.CreatedAtUtc, o.RemovedAtUtc);

    public static MockProviderMessageResponse ToResponse(this MockProviderMessage m)
    {
        IReadOnlyList<string> parameters;
        try
        {
            parameters = JsonSerializer.Deserialize<List<string>>(m.ParametersJson) ?? [];
        }
        catch (JsonException)
        {
            parameters = [];
        }

        return new MockProviderMessageResponse(m.Id, m.Channel.ToString(), m.ProviderMessageId, PrivacyMasking.MaskPhone(m.To), m.Body, m.TemplateName, parameters, m.CreatedAtUtc);
    }
}

internal sealed class NotificationService(
    INotificationOutboxRepository outbox,
    INotificationTemplateRepository templates,
    INotificationOptOutRepository optOuts,
    IMockProviderMessageRepository mockInbox,
    IMessageProviderRegistry providers,
    INotificationWebhookService webhooks,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IValidator<NotificationMessageQuery> queryValidator,
    IValidator<OptOutQuery> optOutQueryValidator,
    IValidator<RequeueNotificationRequest> requeueValidator,
    IValidator<UpdateNotificationTemplateRequest> templateValidator,
    IValidator<PreviewTemplateRequest> previewValidator,
    IValidator<CreateOptOutRequest> optOutValidator,
    IValidator<SimulateStatusRequest> simulateValidator) : INotificationService
{
    // ---- messages ----------------------------------------------------------------------------------------------------

    public async Task<PagedResponse<NotificationMessageResponse>> SearchAsync(NotificationMessageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await queryValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var filter = new NotificationFilter(
            Parse<NotificationChannel>(query.Channel),
            Parse<OutboxStatus>(query.Status),
            Parse<DeliveryStatus>(query.DeliveryStatus),
            Parse<NotificationAudience>(query.Audience),
            string.IsNullOrWhiteSpace(query.TemplateKey) ? null : query.TemplateKey.Trim(),
            query.ParcelId,
            string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim().ToUpperInvariant(),
            query.From is { } from ? NepalDay.StartUtc(from) : null,
            query.To is { } to ? NepalDay.StartUtc(to.AddDays(1)) : null);

        var (items, total) = await outbox.SearchAsync(filter, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return new PagedResponse<NotificationMessageResponse>([.. items.Select(i => i.ToResponse())], query.Page, query.PageSize, total);
    }

    public async Task<NotificationMessageDetailResponse> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await outbox.GetRowAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("Notification", id);
        var logs = await outbox.GetLogsAsync(id, cancellationToken).ConfigureAwait(false);
        return new NotificationMessageDetailResponse(row.ToResponse(), row.Message.RenderedBody, [.. logs.OrderBy(l => l.CreatedAtUtc).Select(l => l.ToResponse())]);
    }

    public async Task<IReadOnlyList<NotificationMessageResponse>> ListForParcelAsync(Guid parcelId, CancellationToken cancellationToken = default)
    {
        var rows = await outbox.ListForParcelAsync(parcelId, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToResponse())];
    }

    public async Task<NotificationSummaryResponse> SummaryAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var today = NepalDay.Today(clock.GetUtcNow());
        var end = to ?? today;
        var start = from ?? end.AddDays(-29);
        if (end < start)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["to"] = ["The end date cannot be before the start date."] });
        }

        if (end.DayNumber - start.DayNumber > 365)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["from"] = ["The period can be at most 366 days."] });
        }

        var counts = await outbox.CountByChannelAsync(NepalDay.StartUtc(start), NepalDay.StartUtc(end.AddDays(1)), cancellationToken).ConfigureAwait(false);
        var channels = new[] { NotificationChannel.Sms, NotificationChannel.WhatsApp }
            .Select(channel => counts.FirstOrDefault(c => c.Channel == channel) ?? new ChannelCounts(channel, 0, 0, 0, 0, 0, 0, 0, 0, 0))
            .Select(c => new NotificationChannelSummary(c.Channel.ToString(), c.Total, c.Queued, c.Retrying, c.Submitted, c.Sent, c.Delivered, c.Read, c.Failed, c.Skipped))
            .ToList();
        var deadLetters = await outbox.CountDeadLettersAsync(cancellationToken).ConfigureAwait(false);
        return new NotificationSummaryResponse(start, end, channels, deadLetters);
    }

    public async Task<NotificationMessageResponse> RequeueAsync(Guid id, RequeueNotificationRequest? request, CancellationToken cancellationToken = default)
    {
        request ??= new RequeueNotificationRequest();
        await requeueValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var message = await outbox.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("Notification", id);
        var now = clock.GetUtcNow();
        message.Requeue(now);
        AddAudit(AuditActions.NotificationRequeued, id, now, new { reason = request.Reason, template = message.TemplateKey, channel = message.Channel.ToString() });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await outbox.GetRowAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("Notification", id)).ToResponse();
    }

    public async Task<NotificationMessageResponse> CancelAsync(Guid id, RequeueNotificationRequest? request, CancellationToken cancellationToken = default)
    {
        request ??= new RequeueNotificationRequest();
        await requeueValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var message = await outbox.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("Notification", id);
        var now = clock.GetUtcNow();
        message.Cancel(now, request.Reason);
        if (NotificationTemplateKeys.IsOneTimeCode(message.TemplateKey))
        {
            message.RedactPayload();
        }

        AddAudit(AuditActions.NotificationCancelled, id, now, new { reason = request.Reason, template = message.TemplateKey, channel = message.Channel.ToString() });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (await outbox.GetRowAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("Notification", id)).ToResponse();
    }

    // ---- templates ---------------------------------------------------------------------------------------------------

    public async Task<NotificationTemplatesResponse> ListTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var all = await templates.ListAsync(cancellationToken).ConfigureAwait(false);
        return new NotificationTemplatesResponse(
            [.. all.OrderBy(t => OrderOf(t.Key)).ThenBy(t => t.Channel).Select(t => t.ToResponse())],
            TemplatePlaceholders.AllowedByKey);
    }

    public async Task<NotificationTemplateResponse> UpdateTemplateAsync(Guid id, UpdateNotificationTemplateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await templateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var template = await templates.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("Template", id);
        var problems = TemplateRenderer.Validate(template.Key, template.Channel, request.Body);
        if (problems.Count > 0)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["body"] = [.. problems] });
        }

        if (request.IsActive
            && template.Channel == NotificationChannel.WhatsApp
            && string.IsNullOrWhiteSpace(request.ProviderTemplateName))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["providerTemplateName"] = ["An active WhatsApp message needs the name of the template Meta approved."],
            });
        }

        var now = clock.GetUtcNow();
        template.Update(request.Body, request.ProviderTemplateName, request.IsActive, now, currentUser.UserId);
        AddAudit(AuditActions.NotificationTemplateUpdated, template.Id, now, new { template.Key, channel = template.Channel.ToString(), template.IsActive });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return template.ToResponse();
    }

    public async Task<PreviewTemplateResponse> PreviewTemplateAsync(PreviewTemplateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await previewValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        return new PreviewTemplateResponse(
            TemplateRenderer.Render(request.Body, TemplatePlaceholders.Samples),
            TemplateRenderer.Parameters(request.Body),
            TemplateRenderer.Validate(request.Key, NotificationChannel.Sms, request.Body));
    }

    // ---- opt-outs ----------------------------------------------------------------------------------------------------

    public async Task<PagedResponse<OptOutResponse>> SearchOptOutsAsync(OptOutQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await optOutQueryValidator.ValidateOrThrowAsync(query, cancellationToken).ConfigureAwait(false);

        var (items, total) = await optOuts.SearchAsync(Parse<NotificationChannel>(query.Channel), query.ActiveOnly, query.Page, query.PageSize, cancellationToken).ConfigureAwait(false);
        return new PagedResponse<OptOutResponse>([.. items.Select(o => o.ToResponse())], query.Page, query.PageSize, total);
    }

    public async Task<OptOutResponse> CreateOptOutAsync(CreateOptOutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await optOutValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var channel = Enum.Parse<NotificationChannel>(request.Channel, ignoreCase: true);
        NepalPhoneNumber.TryNormalize(request.PhoneNumber, out var phone);
        if (await optOuts.FindActiveAsync(phone, channel, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new ConflictException("This number has already opted out of that channel.");
        }

        var now = clock.GetUtcNow();
        var optOut = NotificationOptOut.Create(phone, channel, OptOutSource.Staff, now, currentUser.UserId);
        optOuts.Add(optOut);
        AddAudit(AuditActions.NotificationOptOutAdded, optOut.Id, now, new { channel = channel.ToString(), phone = PrivacyMasking.MaskPhone(phone) });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return optOut.ToResponse();
    }

    public async Task<OptOutResponse> RemoveOptOutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var optOut = await optOuts.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("Opt-out", id);
        var now = clock.GetUtcNow();
        optOut.Remove(now);
        AddAudit(AuditActions.NotificationOptOutRemoved, optOut.Id, now, new { channel = optOut.Channel.ToString(), phone = PrivacyMasking.MaskPhone(optOut.PhoneNumber) });
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return optOut.ToResponse();
    }

    // ---- providers and the mock --------------------------------------------------------------------------------------

    public IReadOnlyList<NotificationProviderInfo> Providers() => providers.Describe();

    public async Task<PagedResponse<MockProviderMessageResponse>> MockInboxAsync(string? channel, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (!providers.IsMockInUse)
        {
            throw new NotFoundException("Test inbox", "mock");
        }

        if (page < 1 || pageSize is < 1 or > 100)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["page"] = ["Page must be 1 or more and page size 1 to 100."] });
        }

        if (!string.IsNullOrWhiteSpace(channel) && !NotificationValidation.IsMessagingChannel(channel))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["channel"] = ["Channel must be Sms or WhatsApp."] });
        }

        var (items, total) = await mockInbox.SearchAsync(Parse<NotificationChannel>(channel), page, pageSize, cancellationToken).ConfigureAwait(false);
        return new PagedResponse<MockProviderMessageResponse>([.. items.Select(m => m.ToResponse())], page, pageSize, total);
    }

    public async Task<WebhookAcceptedResponse> SimulateStatusAsync(string providerMessageId, SimulateStatusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!providers.IsMockInUse)
        {
            throw new NotFoundException("Status simulation", "mock");
        }

        await simulateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);
        var providerName = providerMessageId.StartsWith("mock-sms-", StringComparison.Ordinal) ? "MockSms" : "MockWhatsApp";
        var message = await outbox.GetByProviderMessageIdAsync(providerName, providerMessageId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Message", providerMessageId);
        if (message.Status != OutboxStatus.Submitted || message.ProviderMessageId is null)
        {
            throw new DomainException("Only a message that the mock provider has accepted can be given a simulated status.");
        }

        // Goes through the same code as a real provider's report: stored once, then applied, moving forward only.
        return await webhooks.HandleMockAsync(
            new MockWebhookRequest($"sim-{Guid.NewGuid():N}", message.ProviderMessageId, request.Status, clock.GetUtcNow(), request.Error),
            cancellationToken).ConfigureAwait(false);
    }

    private static int OrderOf(string key)
    {
        for (var i = 0; i < NotificationTemplateKeys.All.Count; i++)
        {
            if (string.Equals(NotificationTemplateKeys.All[i], key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static TEnum? Parse<TEnum>(string? value)
        where TEnum : struct, Enum =>
        string.IsNullOrWhiteSpace(value) ? null : Enum.Parse<TEnum>(value, ignoreCase: true);

    private void AddAudit(string action, Guid entityId, DateTimeOffset now, object values) =>
        auditLogs.Add(AuditLog.Create(
            action,
            "Notification",
            entityId.ToString(),
            currentUser.UserId,
            now,
            newValues: JsonSerializer.Serialize(values),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));
}

/// <summary>A customer's choices about SMS and WhatsApp. Each change adds a consent record: the history is never edited.</summary>
internal sealed class NotificationPreferenceService(
    INotificationConsentRepository consents,
    ICustomerRepository customers,
    IParcelRepository parcels,
    IAuditLogRepository auditLogs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClientInfo client,
    TimeProvider clock,
    IValidator<UpdateNotificationPreferencesRequest> validator) : INotificationPreferenceService
{
    public const string PolicyVersion = "1.0";

    public async Task<NotificationPreferencesResponse> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var customer = await MyCustomerAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(customer.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationPreferencesResponse> UpdateMineAsync(UpdateNotificationPreferencesRequest request, CancellationToken cancellationToken = default)
    {
        var customer = await MyCustomerAsync(cancellationToken).ConfigureAwait(false);
        return await UpdateAsync(customer.Id, request, ConsentSource.Web, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationPreferencesResponse> GetForParcelSenderAsync(Guid parcelId, CancellationToken cancellationToken = default)
    {
        var parcel = await parcels.GetByIdAsync(parcelId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("Parcel", parcelId);
        return await ReadAsync(parcel.SenderCustomerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationPreferencesResponse> UpdateForParcelSenderAsync(Guid parcelId, UpdateNotificationPreferencesRequest request, CancellationToken cancellationToken = default)
    {
        var parcel = await parcels.GetByIdAsync(parcelId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("Parcel", parcelId);
        return await UpdateAsync(parcel.SenderCustomerId, request, ConsentSource.Counter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records the choice at booking time. Only choices that differ from what is recorded are written; the new records are returned.</summary>
    internal static List<CustomerConsent> RecordChanges(
        INotificationConsentRepository repository,
        IReadOnlyCollection<CustomerConsent> existing,
        Guid customerId,
        bool? sms,
        bool? whatsApp,
        ConsentSource source,
        DateTimeOffset now,
        string? ipAddress)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(existing);
        var recorded = new List<CustomerConsent>();
        foreach (var (type, value) in new[] { (ConsentType.SmsNotifications, sms), (ConsentType.WhatsAppNotifications, whatsApp) })
        {
            if (value is { } granted && NotificationEligibility.CurrentConsent(existing, type) != granted)
            {
                var consent = CustomerConsent.Record(customerId, type, granted, PolicyVersion, source, now, ipAddress);
                repository.Add(consent);
                recorded.Add(consent);
            }
        }

        return recorded;
    }

    private async Task<NotificationPreferencesResponse> UpdateAsync(Guid customerId, UpdateNotificationPreferencesRequest request, ConsentSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await validator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var existing = await consents.GetChannelConsentsAsync(customerId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        RecordChanges(consents, existing, customerId, request.Sms, request.WhatsApp, source, now, client.IpAddress);
        auditLogs.Add(AuditLog.Create(
            AuditActions.NotificationPreferencesChanged,
            "Customer",
            customerId.ToString(),
            currentUser.UserId,
            now,
            newValues: JsonSerializer.Serialize(new { sms = request.Sms, whatsApp = request.WhatsApp, source = source.ToString() }),
            ipAddress: client.IpAddress,
            userAgent: client.UserAgent,
            correlationId: client.CorrelationId));
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(customerId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<NotificationPreferencesResponse> ReadAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var all = await consents.GetChannelConsentsAsync(customerId, cancellationToken).ConfigureAwait(false);
        var sms = Latest(all, ConsentType.SmsNotifications);
        var whatsApp = Latest(all, ConsentType.WhatsAppNotifications);
        return new NotificationPreferencesResponse(sms?.IsGranted ?? false, whatsApp?.IsGranted ?? false, sms?.RecordedAtUtc, whatsApp?.RecordedAtUtc);
    }

    private static CustomerConsent? Latest(IEnumerable<CustomerConsent> all, ConsentType type) =>
        all.Where(c => c.Type == type).OrderByDescending(c => c.RecordedAtUtc).ThenByDescending(c => c.Id).FirstOrDefault();

    private async Task<Customer> MyCustomerAsync(CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException("Authentication is required.");
        return await customers.GetByUserIdAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("Customer profile", userId);
    }
}
