using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Notifications;

public enum NotificationLogStatus
{
    /// <summary>The provider reported that it sent the message on.</summary>
    Sent = 1,

    /// <summary>The provider reported that it reached the phone.</summary>
    Delivered = 2,

    /// <summary>Either one of our attempts failed, or the provider reported a failure.</summary>
    Failed = 3,

    /// <summary>One of our attempts: the provider accepted the message. Not a delivery.</summary>
    Submitted = 4,

    /// <summary>The provider reported that the person read it.</summary>
    Read = 5,

    /// <summary>It was deliberately not sent.</summary>
    Skipped = 6,
}

/// <summary>
/// Immutable record of one thing that happened to a notification: an attempt to send it, or a report from the provider. The
/// recipient is stored masked (e.g. 98*****67) to limit personal data.
/// </summary>
public sealed class NotificationLog : Entity
{
    private NotificationLog()
    {
        RecipientMasked = string.Empty;
        Provider = string.Empty;
    }

    public Guid? OutboxMessageId { get; private init; }

    public NotificationChannel Channel { get; private init; }

    public string RecipientMasked { get; private init; }

    public string Provider { get; private init; }

    public string? ProviderMessageId { get; private init; }

    public NotificationLogStatus Status { get; private init; }

    public string? Error { get; private init; }

    /// <summary>For an attempt to send: which attempt this was.</summary>
    public int? AttemptNumber { get; private init; }

    /// <summary>For a provider report: when the provider says it happened.</summary>
    public DateTimeOffset? OccurredAtUtc { get; private init; }

    /// <summary>When we recorded it.</summary>
    public DateTimeOffset CreatedAtUtc { get; private init; }

    public static NotificationLog Create(
        NotificationChannel channel,
        string recipientMasked,
        string provider,
        NotificationLogStatus status,
        DateTimeOffset createdAtUtc,
        Guid? outboxMessageId = null,
        string? providerMessageId = null,
        string? error = null,
        int? attemptNumber = null,
        DateTimeOffset? occurredAtUtc = null) =>
        new()
        {
            OutboxMessageId = outboxMessageId,
            Channel = channel,
            RecipientMasked = Guard.NotBlank(recipientMasked, "Recipient", 100),
            Provider = Guard.NotBlank(provider, "Provider", 50),
            ProviderMessageId = Guard.Optional(providerMessageId, "Provider message id", 100),
            Status = status,
            Error = Guard.Optional(error, "Error", 1000),
            AttemptNumber = attemptNumber,
            OccurredAtUtc = occurredAtUtc,
            CreatedAtUtc = createdAtUtc,
        };
}
