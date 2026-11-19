using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Privacy;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Notifications;

namespace NepalParcel.Application.Notifications;

/// <summary>
/// Sends the messages that are due. For each one: claim it (a database row version stops two workers taking the same
/// message), check consent and opt-outs again (they may have changed since it was queued), fill in the template, hand it to the
/// provider, and record the outcome and an attempt row. A provider accepting a message makes it <c>Submitted</c>, never
/// <c>Delivered</c>: delivery is only ever set by the provider's own reports (see <see cref="NotificationWebhookService"/>).
/// </summary>
/// <remarks>
/// Delivery is at least once. If the process dies after a provider accepted a message but before we saved that, the message is
/// sent again when its lock expires. WhatsApp offers no way to prevent that, so it is accepted and documented.
/// </remarks>
internal sealed partial class OutboxProcessor(
    INotificationOutboxRepository outbox,
    INotificationTemplateRepository templates,
    INotificationOptOutRepository optOuts,
    INotificationConsentRepository consents,
    IMessageProviderRegistry providers,
    IUnitOfWork unitOfWork,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<OutboxProcessor> logger) : IOutboxProcessor
{
    private enum Step
    {
        NotClaimed,
        Submitted,
        Retrying,
        Failed,
        Skipped,
    }

    public async Task<OutboxRunResult> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value.Outbox;
        var ids = await outbox.GetDueIdsAsync(clock.GetUtcNow(), settings.BatchSize, cancellationToken).ConfigureAwait(false);

        int claimed = 0, submitted = 0, retrying = 0, failed = 0, skipped = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = await ProcessOneAsync(id, cancellationToken).ConfigureAwait(false);
            if (step == Step.NotClaimed)
            {
                continue;
            }

            claimed++;
            switch (step)
            {
                case Step.Submitted:
                    submitted++;
                    break;
                case Step.Retrying:
                    retrying++;
                    break;
                case Step.Failed:
                    failed++;
                    break;
                case Step.Skipped:
                    skipped++;
                    break;
            }
        }

        if (claimed > 0)
        {
            LogRun(logger, claimed, submitted, retrying, failed, skipped);
        }

        return new OutboxRunResult(claimed, submitted, retrying, failed, skipped);
    }

    private async Task<Step> ProcessOneAsync(Guid id, CancellationToken cancellationToken)
    {
        var settings = options.Value.Outbox;
        var message = await outbox.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (message is null || !message.IsDue(now))
        {
            return Step.NotClaimed;
        }

        // Claim it. If another worker saved first, the row version no longer matches and this save is refused.
        message.BeginProcessing(now, TimeSpan.FromSeconds(settings.LockSeconds));
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrencyException)
        {
            unitOfWork.DiscardPendingChanges();
            return Step.NotClaimed;
        }

        var isCode = NotificationTemplateKeys.IsOneTimeCode(message.TemplateKey);
        IMessageProvider? provider = null;
        try
        {
            var skipReason = await CheckStillAllowedAsync(message, isCode, cancellationToken).ConfigureAwait(false);
            if (skipReason is not null)
            {
                message.Skip(now, skipReason);
                outbox.Add(NotificationLog.Create(
                    message.Channel, PrivacyMasking.MaskPhone(message.Recipient), "none", NotificationLogStatus.Skipped, now, message.Id, error: skipReason, attemptNumber: message.AttemptCount));
                Redact(message, isCode);
                await SaveAsync(message, cancellationToken).ConfigureAwait(false);
                return Step.Skipped;
            }

            var prepared = await PrepareAsync(message, cancellationToken).ConfigureAwait(false);
            if (prepared.Error is not null)
            {
                return await RecordRejectedAsync(message, "none", prepared.Error, isCode, cancellationToken).ConfigureAwait(false);
            }

            provider = providers.Get(message.Channel);
            var result = await SendSafelyAsync(provider, prepared.Message!, cancellationToken).ConfigureAwait(false);
            return await RecordResultAsync(message, provider, result, prepared.Message!.Body, isCode, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: the message stays locked and is picked up again when the lock expires.
            throw;
        }
#pragma warning disable CA1031 // One message must never stop the others, and whatever went wrong is recorded on the message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogUnexpected(logger, message.Id, ex);

            // Forgetting the unsaved changes also detaches the message, so load it again (it is saved as "processing").
            unitOfWork.DiscardPendingChanges();
            message = await outbox.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (message is null || message.Status != OutboxStatus.Processing)
            {
                return Step.NotClaimed;
            }

            return await RecordResultAsync(
                message,
                provider,
                SendResult.RetryLater($"Unexpected error: {ex.GetType().Name}"),
                null,
                isCode,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Consent can be withdrawn and numbers can opt out between queueing and sending. One-time codes were asked for by the person they go to, so they skip this check.</summary>
    private async Task<string?> CheckStillAllowedAsync(NotificationOutboxMessage message, bool isCode, CancellationToken cancellationToken)
    {
        if (isCode)
        {
            return null;
        }

        var channelName = message.Channel == NotificationChannel.WhatsApp ? "WhatsApp" : "SMS";
        if (await optOuts.FindActiveAsync(message.Recipient, message.Channel, cancellationToken).ConfigureAwait(false) is not null)
        {
            return $"The number has opted out of {channelName} messages.";
        }

        if (message.Audience == NotificationAudience.Sender && message.CustomerId is { } customerId)
        {
            var all = await consents.GetChannelConsentsAsync(customerId, cancellationToken).ConfigureAwait(false);
            if (NotificationEligibility.CurrentConsent(all, NotificationEligibility.ConsentTypeFor(message.Channel)) != true)
            {
                return $"The sender no longer agrees to receive {channelName} messages.";
            }
        }

        return null;
    }

    private sealed record Prepared(OutboundMessage? Message, string? Error);

    private async Task<Prepared> PrepareAsync(NotificationOutboxMessage message, CancellationToken cancellationToken)
    {
        if (!NepalPhoneNumber.TryNormalize(message.Recipient, out var phone) || !NotificationEligibility.IsMobile(phone))
        {
            return new Prepared(null, "The recipient is not a mobile number.");
        }

        var template = await templates.FindActiveAsync(message.TemplateKey, message.Channel, message.Language, cancellationToken).ConfigureAwait(false);
        if (template is null)
        {
            return new Prepared(null, $"There is no active {message.Channel} template '{message.TemplateKey}' ({message.Language}).");
        }

        if (message.Channel == NotificationChannel.WhatsApp && string.IsNullOrWhiteSpace(template.ProviderTemplateName))
        {
            return new Prepared(null, $"The WhatsApp template '{message.TemplateKey}' has no approved template name.");
        }

        var values = TemplateRenderer.ValuesFromPayload(message.PayloadJson);
        return new Prepared(
            new OutboundMessage(
                message.Channel,
                NotificationEligibility.ToInternational(phone),
                message.TemplateKey,
                message.Language,
                TemplateRenderer.Render(template.Body, values),
                template.ProviderTemplateName,
                TemplateRenderer.ParameterValues(template.Body, values),
                message.Id.ToString("N")),
            null);
    }

    private async Task<SendResult> SendSafelyAsync(IMessageProvider provider, OutboundMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SendResult.RetryLater("The provider did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return SendResult.RetryLater($"The provider could not be reached: {ex.GetType().Name}.");
        }
    }

    private async Task<Step> RecordResultAsync(
        NotificationOutboxMessage message,
        IMessageProvider? provider,
        SendResult result,
        string? body,
        bool isCode,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var providerName = provider?.Name ?? "none";
        var masked = PrivacyMasking.MaskPhone(message.Recipient);

        switch (result.Outcome)
        {
            case SendOutcome.Accepted:
                // Accepted is not delivered: the delivery status stays "none" until the provider reports.
                message.MarkSubmitted(now, providerName, result.ProviderMessageId ?? throw new DomainException("The provider accepted a message without an id."), isCode ? null : body);
                outbox.Add(NotificationLog.Create(
                    message.Channel, masked, providerName, NotificationLogStatus.Submitted, now, message.Id, result.ProviderMessageId, attemptNumber: message.AttemptCount));
                Redact(message, isCode);
                await SaveAsync(message, cancellationToken).ConfigureAwait(false);
                return Step.Submitted;

            case SendOutcome.Rejected:
                return await RecordRejectedAsync(message, providerName, result.Error ?? "The provider refused the message.", isCode, cancellationToken).ConfigureAwait(false);

            default:
                var error = Truncate(result.Error ?? "The provider could not take the message now.");
                var delay = RetryBackoff.Delay(message.AttemptCount, options.Value.Outbox);
                message.MarkFailed(now, error, delay);
                outbox.Add(NotificationLog.Create(
                    message.Channel, masked, providerName, NotificationLogStatus.Failed, now, message.Id, error: error, attemptNumber: message.AttemptCount));
                var gaveUp = message.Status == OutboxStatus.DeadLetter;
                if (gaveUp)
                {
                    Redact(message, isCode);
                }

                await SaveAsync(message, cancellationToken).ConfigureAwait(false);
                return gaveUp ? Step.Failed : Step.Retrying;
        }
    }

    private async Task<Step> RecordRejectedAsync(NotificationOutboxMessage message, string providerName, string error, bool isCode, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var text = Truncate(error);
        message.MarkDeadLetter(now, text);
        outbox.Add(NotificationLog.Create(
            message.Channel, PrivacyMasking.MaskPhone(message.Recipient), providerName, NotificationLogStatus.Failed, now, message.Id, error: text, attemptNumber: message.AttemptCount));
        Redact(message, isCode);
        await SaveAsync(message, cancellationToken).ConfigureAwait(false);
        return Step.Failed;
    }

    private async Task SaveAsync(NotificationOutboxMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrencyException)
        {
            // Someone (an administrator, a webhook) changed the message while we were sending. Their change stands; ours is dropped.
            LogLostRace(logger, message.Id);
            unitOfWork.DiscardPendingChanges();
        }
    }

    private static void Redact(NotificationOutboxMessage message, bool isCode)
    {
        if (isCode)
        {
            message.RedactPayload();
        }
    }

    private static string Truncate(string text) => text.Length <= 900 ? text : text[..900];

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification run: {Claimed} claimed, {Submitted} submitted, {Retrying} to retry, {Failed} failed for good, {Skipped} skipped.")]
    private static partial void LogRun(ILogger logger, int claimed, int submitted, int retrying, int failed, int skipped);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while sending notification {MessageId}.")]
    private static partial void LogUnexpected(ILogger logger, Guid messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {MessageId} was changed by someone else while it was being sent; the newer change was kept.")]
    private static partial void LogLostRace(ILogger logger, Guid messageId);
}
