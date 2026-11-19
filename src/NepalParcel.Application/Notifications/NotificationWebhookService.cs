using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Privacy;
using NepalParcel.Contracts.Notifications;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Integrations;
using NepalParcel.Domain.Notifications;

namespace NepalParcel.Application.Notifications;

/// <summary>The names under which providers' reports are stored.</summary>
public static class WebhookProviders
{
    public const string MetaWhatsApp = "MetaWhatsApp";
    public const string Mock = "Mock";
}

/// <summary>
/// Receives what providers tell us about messages. A report is checked (Meta signs each call with the app secret over the exact
/// bytes sent), stored once under its unique event id so a repeat changes nothing, and then applied to the message. Delivery
/// status only ever moves forward and is only ever set here, by a provider's report.
/// </summary>
internal sealed partial class NotificationWebhookService(
    IWebhookEventRepository events,
    INotificationOutboxRepository outbox,
    INotificationOptOutRepository optOuts,
    IMessageProviderRegistry providers,
    IUnitOfWork unitOfWork,
    IOptions<NotificationOptions> options,
    TimeProvider clock,
    ILogger<NotificationWebhookService> logger) : INotificationWebhookService
{
    private const int MaxMetaBodyBytes = 512 * 1024;

    private static readonly string[] StopWords = ["STOP", "STOPALL", "UNSUBSCRIBE", "CANCEL", "END", "QUIT"];
    private static readonly string[] StartWords = ["START", "UNSTOP", "SUBSCRIBE"];

    /// <summary>What we keep of a delivery report: nothing that identifies the person, only the message and what happened.</summary>
    private sealed record StoredReport(string MessageId, string Status, DateTimeOffset OccurredAtUtc, string? Error);

    private enum Applied
    {
        Done,
        Pending,
    }

    // ---- Meta --------------------------------------------------------------------------------------------------------

    public string? VerifyMetaSubscription(string? mode, string? verifyToken, string? challenge)
    {
        var expected = options.Value.WhatsApp.VerifyToken;
        if (!string.Equals(mode, "subscribe", StringComparison.Ordinal)
            || string.IsNullOrEmpty(expected)
            || string.IsNullOrEmpty(verifyToken)
            || string.IsNullOrEmpty(challenge)
            || !FixedTimeEquals(expected, verifyToken))
        {
            return null;
        }

        return challenge;
    }

    public async Task<WebhookAcceptedResponse> HandleMetaAsync(byte[] body, string? signature, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!SignatureIsValid(body, signature, options.Value.WhatsApp.AppSecret))
        {
            LogBadSignature(logger);
            throw new UnauthorizedException("The signature is missing or does not match.");
        }

        if (body.Length > MaxMetaBodyBytes)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["body"] = ["The request is too large."] });
        }

        var parsed = ParseMeta(body);
        return await IngestAsync(WebhookProviders.MetaWhatsApp, parsed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c>, an HMAC-SHA256 of the raw body keyed with the app secret. Compared in
    /// constant time. With no secret configured nothing can be verified, so nothing is accepted.
    /// </summary>
    public static bool SignatureIsValid(byte[] body, string? header, string? appSecret)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrEmpty(appSecret) || string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        const string prefix = "sha256=";
        var value = header.Trim();
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(value[prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private sealed record ParsedEvent(string EventId, string EventType, StoredReport? Report, string? InboundFrom, string? InboundText);

    private static List<ParsedEvent> ParseMeta(byte[] body)
    {
        var result = new List<ParsedEvent>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["body"] = ["The request is not valid JSON."] });
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    ParseStatuses(value, result);
                    ParseInbound(value, result);
                }
            }
        }

        return result;
    }

    private static void ParseStatuses(JsonElement value, List<ParsedEvent> result)
    {
        if (!value.TryGetProperty("statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var status in statuses.EnumerateArray())
        {
            var id = Text(status, "id");
            var state = Text(status, "status");
            if (id is null || state is null)
            {
                continue;
            }

            var occurred = long.TryParse(Text(status, "timestamp"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : DateTimeOffset.MinValue;
            var eventId = $"{id}:{state}";
            result.Add(new ParsedEvent(
                eventId.Length > 100 ? eventId[..100] : eventId,
                $"status:{state}",
                new StoredReport(id, state.ToLowerInvariant(), occurred, MetaError(status)),
                null,
                null));
        }
    }

    private static void ParseInbound(JsonElement value, List<ParsedEvent> result)
    {
        if (!value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var message in messages.EnumerateArray())
        {
            var id = Text(message, "id");
            var from = Text(message, "from");
            string? text = null;
            if (message.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.Object)
            {
                text = Text(textElement, "body");
            }

            if (id is not null && from is not null)
            {
                // The text is kept only when it is one of the opt-out words; anything else a customer writes is not stored.
                var eventId = $"msg:{id}";
                result.Add(new ParsedEvent(eventId.Length > 100 ? eventId[..100] : eventId, "message", null, from, text));
            }
        }
    }

    private static string? MetaError(JsonElement status)
    {
        if (!status.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var error in errors.EnumerateArray())
        {
            var code = Text(error, "code") ?? (error.TryGetProperty("code", out var c) ? c.GetRawText() : null);
            var title = Text(error, "title") ?? Text(error, "message");
            var combined = string.Join(" ", new[] { code is null ? null : $"[{code}]", title }.Where(s => s is not null));
            if (combined.Length > 0)
            {
                return combined.Length <= 450 ? combined : combined[..450];
            }
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    // ---- mock --------------------------------------------------------------------------------------------------------

    public async Task<WebhookAcceptedResponse> HandleMockAsync(MockWebhookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!providers.IsMockInUse)
        {
            throw new NotFoundException("Webhook endpoint", "mock");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.EventId) || request.EventId.Length > 100)
        {
            errors["eventId"] = ["An event id of at most 100 characters is required."];
        }

        if (string.IsNullOrWhiteSpace(request.MessageId) || request.MessageId.Length > 100)
        {
            errors["messageId"] = ["A message id is required."];
        }

        if (ParseStatus(request.Status) is null)
        {
            errors["status"] = ["Status must be sent, delivered, read or failed."];
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        var report = new StoredReport(
            request.MessageId.Trim(),
            request.Status.Trim().ToLowerInvariant(),
            request.OccurredAtUtc ?? clock.GetUtcNow(),
            request.Error);
        return await IngestAsync(
            WebhookProviders.Mock,
            [new ParsedEvent(request.EventId.Trim(), $"status:{report.Status}", report, null, null)],
            cancellationToken).ConfigureAwait(false);
    }

    // ---- common ------------------------------------------------------------------------------------------------------

    private async Task<WebhookAcceptedResponse> IngestAsync(string provider, IReadOnlyList<ParsedEvent> parsed, CancellationToken cancellationToken)
    {
        int applied = 0, duplicates = 0, pending = 0;
        foreach (var item in parsed)
        {
            // The same event id from the same provider is stored once; a repeat is acknowledged and changes nothing.
            if (await events.ExistsAsync(provider, item.EventId, cancellationToken).ConfigureAwait(false))
            {
                duplicates++;
                continue;
            }

            var now = clock.GetUtcNow();
            var payload = item.Report is { } report ? JsonSerializer.Serialize(report) : "{}";
            var stored = WebhookEvent.Receive(provider, item.EventId, item.EventType, payload, signatureValid: true, now);
            events.Add(stored);

            var outcome = item.Report is { } r
                ? await ApplyReportAsync(stored, r, cancellationToken).ConfigureAwait(false)
                : await ApplyInboundAsync(stored, item, cancellationToken).ConfigureAwait(false);

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyException)
            {
                // The message was changed (by the worker or an administrator) while we applied the report. Keep the event, try it again later.
                unitOfWork.DiscardPendingChanges();
                var retry = WebhookEvent.Receive(provider, item.EventId, item.EventType, payload, signatureValid: true, now);
                retry.NoteAttempt("The message was being changed; the report will be tried again.");
                events.Add(retry);
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                outcome = Applied.Pending;
            }

            if (outcome == Applied.Done)
            {
                applied++;
            }
            else
            {
                pending++;
            }
        }

        return new WebhookAcceptedResponse(parsed.Count, applied, duplicates, pending);
    }

    public async Task<int> RetryPendingAsync(CancellationToken cancellationToken = default)
    {
        var since = clock.GetUtcNow().AddHours(-options.Value.Outbox.WebhookRetryHours);
        var done = 0;
        foreach (var provider in new[] { WebhookProviders.MetaWhatsApp, WebhookProviders.Mock })
        {
            var pendingEvents = await events.GetPendingAsync(provider, since, 100, cancellationToken).ConfigureAwait(false);
            foreach (var stored in pendingEvents)
            {
                StoredReport? report;
                try
                {
                    report = JsonSerializer.Deserialize<StoredReport>(stored.PayloadJson);
                }
                catch (JsonException)
                {
                    report = null;
                }

                if (report is null)
                {
                    stored.MarkFailed("The stored report could not be read.");
                    await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    if (await ApplyReportAsync(stored, report, cancellationToken).ConfigureAwait(false) == Applied.Done)
                    {
                        done++;
                    }

                    await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ConcurrencyException)
                {
                    unitOfWork.DiscardPendingChanges();
                }
            }
        }

        return done;
    }

    private async Task<Applied> ApplyReportAsync(WebhookEvent stored, StoredReport report, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var status = ParseStatus(report.Status);
        if (status is null)
        {
            // A state we do not track (Meta also reports "deleted", for example). Nothing to apply; the event is kept as handled.
            stored.MarkProcessed(now);
            return Applied.Done;
        }

        var message = await outbox.GetByProviderMessageIdAsync(ProviderNameFor(stored.Provider, report.MessageId), report.MessageId, cancellationToken).ConfigureAwait(false);
        if (message is null || message.Status != OutboxStatus.Submitted)
        {
            // The report can arrive before we have saved that the provider accepted the message. It is kept and tried again.
            stored.NoteAttempt("No submitted message has this provider id yet.");
            return Applied.Pending;
        }

        var occurred = report.OccurredAtUtc == DateTimeOffset.MinValue ? now : report.OccurredAtUtc;
        var error = status == DeliveryStatus.Failed ? report.Error : null;
        message.ApplyProviderStatus(status.Value, occurred, error);
        outbox.Add(NotificationLog.Create(
            message.Channel,
            PrivacyMasking.MaskPhone(message.Recipient),
            message.Provider ?? stored.Provider,
            status.Value switch
            {
                DeliveryStatus.Sent => NotificationLogStatus.Sent,
                DeliveryStatus.Delivered => NotificationLogStatus.Delivered,
                DeliveryStatus.Read => NotificationLogStatus.Read,
                _ => NotificationLogStatus.Failed,
            },
            now,
            message.Id,
            message.ProviderMessageId,
            error,
            occurredAtUtc: occurred));
        stored.MarkProcessed(now);
        return Applied.Done;
    }

    private async Task<Applied> ApplyInboundAsync(WebhookEvent stored, ParsedEvent inbound, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var word = inbound.InboundText?.Trim().ToUpperInvariant();
        var isStop = word is not null && StopWords.Contains(word, StringComparer.Ordinal);
        var isStart = word is not null && StartWords.Contains(word, StringComparer.Ordinal);
        if ((isStop || isStart) && NepalPhoneNumber.TryNormalize(inbound.InboundFrom, out var phone) && NotificationEligibility.IsMobile(phone))
        {
            var existing = await optOuts.FindActiveAsync(phone, NotificationChannel.WhatsApp, cancellationToken).ConfigureAwait(false);
            if (isStop && existing is null)
            {
                optOuts.Add(NotificationOptOut.Create(phone, NotificationChannel.WhatsApp, OptOutSource.InboundMessage, now));
                LogOptOut(logger, PrivacyMasking.MaskPhone(phone), true);
            }
            else if (isStart && existing is not null)
            {
                existing.Remove(now);
                LogOptOut(logger, PrivacyMasking.MaskPhone(phone), false);
            }
        }

        stored.MarkProcessed(now);
        return Applied.Done;
    }

    public static DeliveryStatus? ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "sent" => DeliveryStatus.Sent,
        "delivered" => DeliveryStatus.Delivered,
        "read" => DeliveryStatus.Read,
        "failed" => DeliveryStatus.Failed,
        _ => null,
    };

    /// <summary>Mock ids tell which mock provider issued them; for Meta the provider is fixed.</summary>
    private static string ProviderNameFor(string webhookProvider, string messageId) =>
        webhookProvider == WebhookProviders.Mock
            ? messageId.StartsWith("mock-sms-", StringComparison.Ordinal) ? "MockSms" : "MockWhatsApp"
            : webhookProvider;

    private static bool FixedTimeEquals(string expected, string provided) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided));

    [LoggerMessage(Level = LogLevel.Warning, Message = "A WhatsApp webhook was refused: its signature did not match.")]
    private static partial void LogBadSignature(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp opt-out for {Phone}: {OptedOut}.")]
    private static partial void LogOptOut(ILogger logger, string phone, bool optedOut);
}
