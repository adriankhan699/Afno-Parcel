namespace NepalParcel.Contracts.Notifications;

// ---- messages and their states ---------------------------------------------------------------------------------------

/// <summary>
/// One notification to one person on one channel (SMS or WhatsApp). Four things are tracked separately and never confused:
/// <c>Status</c> is what we have done with it (queued, handed to the provider, given up, skipped);
/// <c>DeliveryStatus</c> is only what the <b>provider reported afterwards</b> (sent, delivered, read, failed). A message the
/// provider merely accepted is <c>Submitted</c> with a delivery status of <c>None</c>, never <c>Delivered</c>.
/// Status is one of Pending, Processing, Submitted, Failed (will retry), DeadLetter (gave up), Skipped (not sent: consent,
/// opt-out or no template) or Cancelled. DeliveryStatus is None until the provider reports: then Sent, Delivered, Read or Failed.
/// </summary>
public sealed record NotificationMessageResponse(
    Guid Id,
    string Channel,
    string Audience,
    string TemplateKey,
    string RecipientMasked,
    string Status,
    string DeliveryStatus,
    string? Provider,
    string? ProviderMessageId,
    Guid? ParcelId,
    string? TrackingNumber,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset NextAttemptAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? SentAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    DateTimeOffset? ReadAtUtc,
    DateTimeOffset? FailedAtUtc,
    string? SkipReason,
    string? LastError,
    string? DeliveryError);

/// <summary>One row of a message's history: an attempt to send it, or something the provider told us about it.</summary>
public sealed record NotificationAttemptResponse(
    Guid Id,
    string Status,
    string Provider,
    string? ProviderMessageId,
    int? AttemptNumber,
    string? Detail,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? OccurredAtUtc);

public sealed record NotificationMessageDetailResponse(
    NotificationMessageResponse Message,
    string? RenderedBody,
    IReadOnlyList<NotificationAttemptResponse> Attempts);

/// <summary>Filters for the message list.</summary>
public sealed record NotificationMessageQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    /// <summary>Sms or WhatsApp.</summary>
    public string? Channel { get; init; }

    public string? Status { get; init; }

    public string? DeliveryStatus { get; init; }

    /// <summary>Sender or Receiver.</summary>
    public string? Audience { get; init; }

    public string? TemplateKey { get; init; }

    public Guid? ParcelId { get; init; }

    /// <summary>A tracking number (or the start of one).</summary>
    public string? Search { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }
}

public sealed record NotificationChannelSummary(
    string Channel,
    int Total,
    int Queued,
    int Retrying,
    int Submitted,
    int Sent,
    int Delivered,
    int Read,
    int Failed,
    int Skipped);

/// <summary>
/// Messages created in the period, counted by what has happened to them. <c>Submitted</c> means accepted by the provider and
/// not yet reported on; <c>Delivered</c> includes the ones that were also read.
/// </summary>
public sealed record NotificationSummaryResponse(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<NotificationChannelSummary> Channels,
    int DeadLetters);

public sealed record RequeueNotificationRequest(string? Reason = null);

// ---- templates -------------------------------------------------------------------------------------------------------

/// <summary>
/// A message text. <c>Body</c> uses named placeholders such as <c>{tracking}</c>. For WhatsApp the text is only our copy: Meta
/// sends the template it has approved under <c>ProviderTemplateName</c>, filling its numbered fields with the placeholders in
/// the order they first appear, so the approved template must have the same fields in the same order.
/// </summary>
public sealed record NotificationTemplateResponse(
    Guid Id,
    string Key,
    string Channel,
    string Language,
    string Body,
    string? ProviderTemplateName,
    IReadOnlyList<string> Parameters,
    bool IsActive,
    int Length,
    DateTimeOffset UpdatedAtUtc);

public sealed record NotificationTemplatesResponse(
    IReadOnlyList<NotificationTemplateResponse> Templates,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedPlaceholders);

public sealed record UpdateNotificationTemplateRequest(string Body, string? ProviderTemplateName, bool IsActive);

public sealed record PreviewTemplateRequest(string Key, string Body);

/// <summary>The text with sample values filled in, and anything wrong with the template.</summary>
public sealed record PreviewTemplateResponse(string Text, IReadOnlyList<string> Parameters, IReadOnlyList<string> Problems);

// ---- consent and opt-outs --------------------------------------------------------------------------------------------

/// <summary>Whether the customer has agreed to receive updates by SMS and by WhatsApp. Off until they say yes.</summary>
public sealed record NotificationPreferencesResponse(
    bool Sms,
    bool WhatsApp,
    DateTimeOffset? SmsDecidedAtUtc,
    DateTimeOffset? WhatsAppDecidedAtUtc);

/// <summary>Changes only what is given. Each change adds a new consent record; the history is kept.</summary>
public sealed record UpdateNotificationPreferencesRequest(bool? Sms, bool? WhatsApp);

/// <summary>A phone number that has asked not to be messaged on a channel (for example by replying STOP).</summary>
public sealed record OptOutResponse(
    Guid Id,
    string PhoneMasked,
    string Channel,
    string Source,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RemovedAtUtc);

public sealed record CreateOptOutRequest(string PhoneNumber, string Channel);

public sealed record OptOutQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    public string? Channel { get; init; }

    /// <summary>true = only the opt-outs that are still in force (the default).</summary>
    public bool ActiveOnly { get; init; } = true;
}

// ---- providers and the mock ------------------------------------------------------------------------------------------

/// <summary>Which provider is in use for a channel. Never contains credentials.</summary>
public sealed record NotificationProviderInfo(
    string Channel,
    string Provider,
    bool IsMock,
    bool IsConfigured,
    string Detail);

/// <summary>What the mock provider "sent" (development and test only): the text the person would have received.</summary>
public sealed record MockProviderMessageResponse(
    Guid Id,
    string Channel,
    string ProviderMessageId,
    string ToMasked,
    string Body,
    string? TemplateName,
    IReadOnlyList<string> Parameters,
    DateTimeOffset CreatedAtUtc);

/// <summary>Makes the mock provider report a status for a message, as a real provider's webhook would.</summary>
public sealed record SimulateStatusRequest(string Status, string? Error = null);

// ---- webhooks --------------------------------------------------------------------------------------------------------

/// <summary>The body of a delivery report sent by the mock provider (the real providers have their own formats).</summary>
public sealed record MockWebhookRequest(string EventId, string MessageId, string Status, DateTimeOffset? OccurredAtUtc = null, string? Error = null);

/// <summary>What the server did with a webhook. Duplicates are accepted and ignored.</summary>
public sealed record WebhookAcceptedResponse(int Received, int Applied, int Duplicates, int Pending);
