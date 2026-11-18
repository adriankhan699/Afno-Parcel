using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Notifications;

public enum NotificationChannel
{
    Sms = 1,
    Email = 2,
}

public enum OutboxStatus
{
    Pending = 1,
    Processing = 2,
    Sent = 3,
    Failed = 4,
    DeadLetter = 5,
}

/// <summary>
/// Transactional outbox row: written in the same transaction as the business change, then delivered by a background
/// worker with retries. This guarantees notifications are neither lost nor sent for rolled-back changes.
/// Recipient and payload may contain personal data — never log them.
/// </summary>
public sealed class NotificationOutboxMessage : Entity
{
    public const int DefaultMaxAttempts = 5;

    private NotificationOutboxMessage()
    {
        Recipient = string.Empty;
        TemplateKey = string.Empty;
        PayloadJson = "{}";
    }

    public NotificationChannel Channel { get; private set; }

    public string Recipient { get; private set; }

    public string TemplateKey { get; private set; }

    public string PayloadJson { get; private set; }

    public Guid? ParcelId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public OutboxStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public int MaxAttempts { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    /// <summary>A worker owns the message until this time; after it passes another worker may retry.</summary>
    public DateTimeOffset? LockedUntilUtc { get; private set; }

    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public string? LastError { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static NotificationOutboxMessage Enqueue(
        NotificationChannel channel,
        string recipient,
        string templateKey,
        string payloadJson,
        DateTimeOffset nowUtc,
        Guid? parcelId = null,
        Guid? customerId = null,
        int maxAttempts = DefaultMaxAttempts)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new DomainException($"'{channel}' is not a valid notification channel.");
        }

        if (maxAttempts < 1)
        {
            throw new DomainException("Max attempts must be at least 1.");
        }

        return new NotificationOutboxMessage
        {
            Channel = channel,
            Recipient = Guard.NotBlank(recipient, "Recipient", 256),
            TemplateKey = Guard.NotBlank(templateKey, "Template key", 100),
            PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            ParcelId = parcelId,
            CustomerId = customerId,
            Status = OutboxStatus.Pending,
            MaxAttempts = maxAttempts,
            CreatedAtUtc = nowUtc,
            NextAttemptAtUtc = nowUtc,
        };
    }

    public void BeginProcessing(DateTimeOffset nowUtc, TimeSpan lockFor)
    {
        if (Status is not (OutboxStatus.Pending or OutboxStatus.Failed))
        {
            throw new DomainException($"A message in status '{Status}' cannot be processed.");
        }

        Status = OutboxStatus.Processing;
        AttemptCount++;
        LockedUntilUtc = nowUtc.Add(lockFor);
    }

    public void MarkSent(DateTimeOffset nowUtc)
    {
        Status = OutboxStatus.Sent;
        ProcessedAtUtc = nowUtc;
        LockedUntilUtc = null;
        LastError = null;
    }

    public void MarkFailed(DateTimeOffset nowUtc, string error, TimeSpan retryDelay)
    {
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
}
