using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Notifications;

public enum NotificationChannel
{
    Sms = 1,
    Email = 2,
    WhatsApp = 3,
}

/// <summary>Who a parcel notification is for.</summary>
public enum NotificationAudience
{
    /// <summary>The customer who sent the parcel. Needs their consent for the channel.</summary>
    Sender = 1,

    /// <summary>The person the parcel is going to. They are not a customer: they can opt out of a channel, nothing more.</summary>
    Receiver = 2,
}

/// <summary>
/// What <b>we</b> have done with a message. This says nothing about whether the person got it: for that, see
/// <see cref="DeliveryStatus"/>, which only the provider's own reports can change.
/// </summary>
public enum OutboxStatus
{
    /// <summary>Waiting for the background worker.</summary>
    Pending = 1,

    /// <summary>A worker is sending it now.</summary>
    Processing = 2,

    /// <summary>The provider accepted it and gave us a message id. It has <b>not</b> been delivered as far as we know.</summary>
    Submitted = 3,

    /// <summary>An attempt failed; it will be tried again after a delay.</summary>
    Failed = 4,

    /// <summary>All attempts failed, or the failure was permanent. Someone can look at it and requeue it.</summary>
    DeadLetter = 5,

    /// <summary>Deliberately not sent: no consent, an opt-out, no template, a number that cannot receive messages.</summary>
    Skipped = 6,

    Cancelled = 7,
}

/// <summary>What the provider has told us about a submitted message, and nothing else. <c>None</c> until it reports.</summary>
public enum DeliveryStatus
{
    None = 0,
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4,
}

/// <summary>
/// Transactional outbox row: written in the same transaction as the business change, then delivered by a background
/// worker with retries. This guarantees notifications are neither lost nor sent for rolled-back changes.
/// Recipient and payload may contain personal data — never log them.
/// </summary>
public sealed class NotificationOutboxMessage : Entity
{
    public const int DefaultMaxAttempts = 5;
    public const string DefaultLanguage = "en";

    private NotificationOutboxMessage()
    {
        Recipient = string.Empty;
        TemplateKey = string.Empty;
        PayloadJson = "{}";
        Language = DefaultLanguage;
    }

    public NotificationChannel Channel { get; private set; }

    public NotificationAudience Audience { get; private set; }

    /// <summary>The phone number as stored on the parcel (Nepali national form, for example 9841234567). Providers add the country code.</summary>
    public string Recipient { get; private set; }

    public string TemplateKey { get; private set; }

    public string Language { get; private set; }

    public string PayloadJson { get; private set; }

    public Guid? ParcelId { get; private set; }

    public Guid? CustomerId { get; private set; }

    /// <summary>
    /// For a parcel event: the id of the status-history row that caused it. With the audience and channel it is unique, so the
    /// same event can never be queued twice.
    /// </summary>
    public string? DedupeKey { get; private set; }

    public OutboxStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public int MaxAttempts { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    /// <summary>A worker owns the message until this time; after it passes another worker may retry.</summary>
    public DateTimeOffset? LockedUntilUtc { get; private set; }

    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public string? LastError { get; private set; }

    public string? SkipReason { get; private set; }

    // ---- what the provider accepted ---------------------------------------------------------------------------------------

    public string? Provider { get; private set; }

    /// <summary>The provider's own id for the message (a WhatsApp <c>wamid</c>, an SMS gateway's message id).</summary>
    public string? ProviderMessageId { get; private set; }

    /// <summary>The text we asked the provider to send (tracking numbers and links only: no phone numbers or addresses).</summary>
    public string? RenderedBody { get; private set; }

    public DateTimeOffset? SubmittedAtUtc { get; private set; }

    // ---- what the provider reported afterwards ------------------------------------------------------------------------------

    public DeliveryStatus DeliveryStatus { get; private set; }

    public DateTimeOffset? SentAtUtc { get; private set; }

    public DateTimeOffset? DeliveredAtUtc { get; private set; }

    public DateTimeOffset? ReadAtUtc { get; private set; }

    public DateTimeOffset? FailedAtUtc { get; private set; }

    public string? DeliveryError { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>A system message such as a one-time code, for the person who asked for it. No consent needed; it goes to the receiver's phone.</summary>
    public static NotificationOutboxMessage Enqueue(
        NotificationChannel channel,
        string recipient,
        string templateKey,
        string payloadJson,
        DateTimeOffset nowUtc,
        Guid? parcelId = null,
        Guid? customerId = null,
        int maxAttempts = DefaultMaxAttempts) =>
        Create(channel, NotificationAudience.Receiver, recipient, templateKey, payloadJson, nowUtc, parcelId, customerId, null, maxAttempts, DefaultLanguage);

    /// <summary>A message about a parcel event, ready for the worker.</summary>
    public static NotificationOutboxMessage EnqueueForParcel(
        NotificationChannel channel,
        NotificationAudience audience,
        string recipient,
        string templateKey,
        string payloadJson,
        DateTimeOffset nowUtc,
        Guid parcelId,
        Guid? customerId,
        string dedupeKey,
        int maxAttempts,
        string language = DefaultLanguage) =>
        Create(channel, audience, recipient, templateKey, payloadJson, nowUtc, parcelId, customerId, Guard.NotBlank(dedupeKey, "Dedupe key", 100), maxAttempts, language);

    /// <summary>A message that was never going to be sent, kept so it is clear why a person did not hear from us.</summary>
    public static NotificationOutboxMessage CreateSkipped(
        NotificationChannel channel,
        NotificationAudience audience,
        string recipient,
        string templateKey,
        DateTimeOffset nowUtc,
        string reason,
        Guid parcelId,
        Guid? customerId,
        string dedupeKey,
        int maxAttempts,
        string language = DefaultLanguage)
    {
        var message = Create(channel, audience, recipient, templateKey, "{}", nowUtc, parcelId, customerId, Guard.NotBlank(dedupeKey, "Dedupe key", 100), maxAttempts, language);
        message.Status = OutboxStatus.Skipped;
        message.SkipReason = Guard.NotBlank(reason, "Reason", 300);
        message.ProcessedAtUtc = nowUtc;
        return message;
    }

    private static NotificationOutboxMessage Create(
        NotificationChannel channel,
        NotificationAudience audience,
        string recipient,
        string templateKey,
        string payloadJson,
        DateTimeOffset nowUtc,
        Guid? parcelId,
        Guid? customerId,
        string? dedupeKey,
        int maxAttempts,
        string language)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new DomainException($"'{channel}' is not a valid notification channel.");
        }

        if (!Enum.IsDefined(audience))
        {
            throw new DomainException($"'{audience}' is not a valid audience.");
        }

        if (maxAttempts < 1)
        {
            throw new DomainException("Max attempts must be at least 1.");
        }

        return new NotificationOutboxMessage
        {
            Channel = channel,
            Audience = audience,
            Recipient = Guard.NotBlank(recipient, "Recipient", 256),
            TemplateKey = Guard.NotBlank(templateKey, "Template key", 100),
            Language = Guard.NotBlank(language, "Language", 10),
            PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            ParcelId = parcelId,
            CustomerId = customerId,
            DedupeKey = dedupeKey,
            Status = OutboxStatus.Pending,
            MaxAttempts = maxAttempts,
            CreatedAtUtc = nowUtc,
            NextAttemptAtUtc = nowUtc,
        };
    }

    /// <summary>A message that is waiting, is due to be retried, or was left locked by a worker that never finished.</summary>
    public bool IsDue(DateTimeOffset nowUtc) =>
        (Status is OutboxStatus.Pending or OutboxStatus.Failed && NextAttemptAtUtc <= nowUtc)
        || (Status == OutboxStatus.Processing && LockedUntilUtc is { } until && until <= nowUtc);

    public void BeginProcessing(DateTimeOffset nowUtc, TimeSpan lockFor)
    {
        if (!IsDue(nowUtc))
        {
            throw new DomainException($"A message in status '{Status}' is not due for processing.");
        }

        Status = OutboxStatus.Processing;
        AttemptCount++;
        LockedUntilUtc = nowUtc.Add(lockFor);
    }

    /// <summary>The provider accepted the message and gave it an id. This is <b>not</b> a delivery: that is reported later, if at all.</summary>
    public void MarkSubmitted(DateTimeOffset nowUtc, string provider, string providerMessageId, string? renderedBody)
    {
        RequireProcessing();
        Status = OutboxStatus.Submitted;
        Provider = Guard.NotBlank(provider, "Provider", 50);
        ProviderMessageId = Guard.NotBlank(providerMessageId, "Provider message id", 100);
        RenderedBody = Guard.Optional(renderedBody, "Rendered body", 1000);
        SubmittedAtUtc = nowUtc;
        ProcessedAtUtc = nowUtc;
        LockedUntilUtc = null;
        LastError = null;
    }

    public void MarkFailed(DateTimeOffset nowUtc, string error, TimeSpan retryDelay)
    {
        RequireProcessing();
        LastError = Guard.NotBlank(error, "Error", 1000);
        LockedUntilUtc = null;
        if (AttemptCount >= MaxAttempts)
        {
            Status = OutboxStatus.DeadLetter;
            ProcessedAtUtc = nowUtc;
            return;
        }

        Status = OutboxStatus.Failed;
        NextAttemptAtUtc = nowUtc.Add(retryDelay);
    }

    /// <summary>The failure cannot be cured by trying again (an invalid number, a template that does not exist).</summary>
    public void MarkDeadLetter(DateTimeOffset nowUtc, string error)
    {
        RequireProcessing();
        LastError = Guard.NotBlank(error, "Error", 1000);
        LockedUntilUtc = null;
        Status = OutboxStatus.DeadLetter;
        ProcessedAtUtc = nowUtc;
    }

    /// <summary>Decided not to send it (consent withdrawn since it was queued, opt-out, no template).</summary>
    public void Skip(DateTimeOffset nowUtc, string reason)
    {
        if (Status is not (OutboxStatus.Pending or OutboxStatus.Processing or OutboxStatus.Failed))
        {
            throw new DomainException($"A message in status '{Status}' cannot be skipped.");
        }

        Status = OutboxStatus.Skipped;
        SkipReason = Guard.NotBlank(reason, "Reason", 300);
        ProcessedAtUtc = nowUtc;
        LockedUntilUtc = null;
    }

    public void Cancel(DateTimeOffset nowUtc, string? reason)
    {
        if (Status is not (OutboxStatus.Pending or OutboxStatus.Failed))
        {
            throw new DomainException($"A message in status '{Status}' cannot be cancelled.");
        }

        Status = OutboxStatus.Cancelled;
        SkipReason = Guard.Optional(reason, "Reason", 300) ?? "Cancelled by staff";
        ProcessedAtUtc = nowUtc;
    }

    /// <summary>Gives a message that failed for good another full set of attempts.</summary>
    public void Requeue(DateTimeOffset nowUtc)
    {
        if (Status is not (OutboxStatus.DeadLetter or OutboxStatus.Failed))
        {
            throw new DomainException($"A message in status '{Status}' cannot be requeued.");
        }

        Status = OutboxStatus.Pending;
        AttemptCount = 0;
        NextAttemptAtUtc = nowUtc;
        ProcessedAtUtc = null;
        LockedUntilUtc = null;
    }

    /// <summary>
    /// Applies what the provider reported. Reports arrive late, twice and out of order, so the state only moves forward
    /// (sent → delivered → read), a report we already have changes nothing, and a failure never overwrites a delivery.
    /// Returns whether anything changed.
    /// </summary>
    public bool ApplyProviderStatus(DeliveryStatus reported, DateTimeOffset occurredAtUtc, string? error = null)
    {
        if (Status != OutboxStatus.Submitted)
        {
            throw new DomainException("Only a message the provider has accepted can receive delivery reports.");
        }

        switch (reported)
        {
            case DeliveryStatus.Sent when DeliveryStatus == DeliveryStatus.None:
                DeliveryStatus = DeliveryStatus.Sent;
                SentAtUtc = occurredAtUtc;
                return true;

            case DeliveryStatus.Delivered when DeliveryStatus is DeliveryStatus.None or DeliveryStatus.Sent or DeliveryStatus.Failed:
                DeliveryStatus = DeliveryStatus.Delivered;
                DeliveredAtUtc = occurredAtUtc;
                DeliveryError = null;
                return true;

            case DeliveryStatus.Read when DeliveryStatus != DeliveryStatus.Read:
                DeliveryStatus = DeliveryStatus.Read;
                ReadAtUtc = occurredAtUtc;
                DeliveredAtUtc ??= occurredAtUtc;
                DeliveryError = null;
                return true;

            case DeliveryStatus.Failed when DeliveryStatus is DeliveryStatus.None or DeliveryStatus.Sent:
                DeliveryStatus = DeliveryStatus.Failed;
                FailedAtUtc = occurredAtUtc;
                DeliveryError = Guard.Optional(error, "Delivery error", 500) ?? "The provider reported that delivery failed.";
                return true;

            case DeliveryStatus.None:
                throw new DomainException("A provider cannot report 'none'.");

            default:
                return false;
        }
    }

    /// <summary>Removes what the payload carried. Used for one-time codes once the message has been sent or given up on.</summary>
    public void RedactPayload() => PayloadJson = "{}";

    private void RequireProcessing()
    {
        if (Status != OutboxStatus.Processing)
        {
            throw new DomainException($"Only a message that is being processed can do this. This one is '{Status}'.");
        }
    }
}
