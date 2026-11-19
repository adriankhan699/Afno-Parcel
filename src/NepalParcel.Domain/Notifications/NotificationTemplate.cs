using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Notifications;

/// <summary>The keys of the messages the system sends. Parcel events come first, then one-time codes.</summary>
public static class NotificationTemplateKeys
{
    public const string ParcelBooked = "parcel-booked";
    public const string ParcelPickedUp = "parcel-picked-up";
    public const string ParcelShipped = "parcel-shipped";
    public const string ParcelInTransit = "parcel-in-transit";
    public const string ParcelAtWarehouse = "parcel-at-warehouse";
    public const string ParcelOutForDelivery = "parcel-out-for-delivery";
    public const string ParcelDelivered = "parcel-delivered";
    public const string ParcelDeliveryFailed = "parcel-delivery-failed";
    public const string ParcelReturned = "parcel-returned";

    /// <summary>A code sent to the receiver so they can see the parcel's details.</summary>
    public const string TrackingOtp = "tracking-otp";

    /// <summary>A code the receiver reads out to the delivery agent.</summary>
    public const string DeliveryOtp = "delivery-otp";

    public static IReadOnlyList<string> ParcelEvents { get; } =
    [
        ParcelBooked, ParcelPickedUp, ParcelShipped, ParcelInTransit, ParcelAtWarehouse,
        ParcelOutForDelivery, ParcelDelivered, ParcelDeliveryFailed, ParcelReturned,
    ];

    public static IReadOnlyList<string> All { get; } = [.. ParcelEvents, TrackingOtp, DeliveryOtp];

    /// <summary>Codes are sent to whoever asked for them, with no consent and no opt-out check, and only ever by SMS.</summary>
    public static bool IsOneTimeCode(string key) => key is TrackingOtp or DeliveryOtp;
}

/// <summary>
/// The text of one message on one channel in one language. Placeholders are written <c>{name}</c>. WhatsApp only lets a business
/// start a conversation with a template that Meta has approved, so for WhatsApp the text here is our copy and
/// <see cref="ProviderTemplateName"/> is the approved template that is actually sent, with these placeholders as its numbered
/// fields in order of first appearance.
/// </summary>
public sealed class NotificationTemplate : Entity
{
    public const int MaxBodyLength = 1000;

    private NotificationTemplate()
    {
        Key = string.Empty;
        Language = NotificationOutboxMessage.DefaultLanguage;
        Body = string.Empty;
    }

    public string Key { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public string Language { get; private set; }

    public string Body { get; private set; }

    /// <summary>The name of the template approved by the provider (WhatsApp). Empty for SMS.</summary>
    public string? ProviderTemplateName { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static NotificationTemplate Create(
        string key,
        NotificationChannel channel,
        string language,
        string body,
        string? providerTemplateName,
        DateTimeOffset nowUtc)
    {
        if (channel is not (NotificationChannel.Sms or NotificationChannel.WhatsApp))
        {
            throw new DomainException("Templates exist for SMS and WhatsApp only.");
        }

        return new NotificationTemplate
        {
            Key = Guard.NotBlank(key, "Key", 100),
            Channel = channel,
            Language = Guard.NotBlank(language, "Language", 10),
            Body = Guard.NotBlank(body, "Body", MaxBodyLength),
            ProviderTemplateName = Guard.Optional(providerTemplateName, "Provider template name", 100),
            IsActive = true,
            UpdatedAtUtc = nowUtc,
        };
    }

    public void Update(string body, string? providerTemplateName, bool isActive, DateTimeOffset nowUtc, Guid? userId)
    {
        Body = Guard.NotBlank(body, "Body", MaxBodyLength);
        ProviderTemplateName = Guard.Optional(providerTemplateName, "Provider template name", 100);
        IsActive = isActive;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = userId;
    }
}

public enum OptOutSource
{
    /// <summary>The person replied STOP (or the provider told us they blocked us).</summary>
    InboundMessage = 1,

    /// <summary>Staff recorded the request.</summary>
    Staff = 2,
}

/// <summary>
/// A phone number that asked not to be messaged on a channel. Applies to everyone with that number, customer or not, and
/// overrides consent. Lifting it (START, or staff) keeps the row and sets <see cref="RemovedAtUtc"/>, so the history stays.
/// </summary>
public sealed class NotificationOptOut : Entity
{
    private NotificationOptOut()
    {
        PhoneNumber = string.Empty;
    }

    public string PhoneNumber { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public OptOutSource Source { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? RemovedAtUtc { get; private set; }

    public Guid? RecordedByUserId { get; private set; }

    public bool IsActive => RemovedAtUtc is null;

    public static NotificationOptOut Create(string phoneNumber, NotificationChannel channel, OptOutSource source, DateTimeOffset nowUtc, Guid? recordedByUserId = null)
    {
        if (channel is not (NotificationChannel.Sms or NotificationChannel.WhatsApp))
        {
            throw new DomainException("Opt-outs exist for SMS and WhatsApp only.");
        }

        if (!Enum.IsDefined(source))
        {
            throw new DomainException($"'{source}' is not a valid opt-out source.");
        }

        return new NotificationOptOut
        {
            PhoneNumber = Guard.NormalizePhone(phoneNumber),
            Channel = channel,
            Source = source,
            CreatedAtUtc = nowUtc,
            RecordedByUserId = recordedByUserId,
        };
    }

    public void Remove(DateTimeOffset nowUtc)
    {
        if (RemovedAtUtc is not null)
        {
            throw new DomainException("This opt-out has already been removed.");
        }

        RemovedAtUtc = nowUtc;
    }
}

/// <summary>
/// What the mock providers "sent" (development and test only), kept so a person can read it on screen instead of on a phone.
/// Never written when a real provider is in use.
/// </summary>
public sealed class MockProviderMessage : Entity
{
    private MockProviderMessage()
    {
        ProviderMessageId = string.Empty;
        To = string.Empty;
        Body = string.Empty;
        ParametersJson = "[]";
    }

    public NotificationChannel Channel { get; private init; }

    public string ProviderMessageId { get; private init; }

    public string To { get; private init; }

    public string Body { get; private init; }

    public string? TemplateName { get; private init; }

    public string ParametersJson { get; private init; }

    public DateTimeOffset CreatedAtUtc { get; private init; }

    public static MockProviderMessage Create(
        NotificationChannel channel,
        string providerMessageId,
        string to,
        string body,
        string? templateName,
        string parametersJson,
        DateTimeOffset nowUtc) =>
        new()
        {
            Channel = channel,
            ProviderMessageId = Guard.NotBlank(providerMessageId, "Provider message id", 100),
            To = Guard.NotBlank(to, "To", 30),
            Body = Guard.NotBlank(body, "Body", 2000),
            TemplateName = Guard.Optional(templateName, "Template name", 100),
            ParametersJson = string.IsNullOrWhiteSpace(parametersJson) ? "[]" : parametersJson,
            CreatedAtUtc = nowUtc,
        };
}
