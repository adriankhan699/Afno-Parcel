using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Notifications;

public enum NotificationLogStatus
{
    Sent = 1,
    Delivered = 2,
    Failed = 3,
}

/// <summary>Immutable record of one delivery attempt. The recipient is stored masked (e.g. 98*****67) to limit personal data.</summary>
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

    public DateTimeOffset CreatedAtUtc { get; private init; }

    public static NotificationLog Create(
        NotificationChannel channel,
        string recipientMasked,
        string provider,
        NotificationLogStatus status,
        DateTimeOffset createdAtUtc,
        Guid? outboxMessageId = null,
        string? providerMessageId = null,
        string? error = null) =>
        new()
        {
            OutboxMessageId = outboxMessageId,
            Channel = channel,
            RecipientMasked = Guard.NotBlank(recipientMasked, "Recipient", 100),
            Provider = Guard.NotBlank(provider, "Provider", 50),
            ProviderMessageId = Guard.Optional(providerMessageId, "Provider message id", 100),
            Status = status,
            Error = Guard.Optional(error, "Error", 1000),
            CreatedAtUtc = createdAtUtc,
        };
}
