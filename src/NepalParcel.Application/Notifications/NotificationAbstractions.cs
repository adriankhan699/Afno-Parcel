using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Notifications;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Integrations;
using NepalParcel.Domain.Notifications;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.Application.Notifications;

// ---- settings --------------------------------------------------------------------------------------------------------

/// <summary>
/// Notification settings (configuration section <c>Notifications</c>). Nothing in the committed files is a secret: the Meta
/// access token, app secret and verify token come from user-secrets or environment variables.
/// </summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Master switch. When off no parcel notification is queued at all (one-time codes are still queued).</summary>
    public bool Enabled { get; set; } = true;

    [ValidateObjectMembers]
    public WhatsAppOptions WhatsApp { get; set; } = new();

    [ValidateObjectMembers]
    public SmsOptions Sms { get; set; } = new();

    [ValidateObjectMembers]
    public OutboxOptions Outbox { get; set; } = new();
}

public sealed class WhatsAppOptions
{
    public const string MockProvider = "Mock";
    public const string MetaProvider = "Meta";

    /// <summary><c>Mock</c> (default: nothing leaves this application) or <c>Meta</c> (the WhatsApp Business Cloud API).</summary>
    [Required]
    [RegularExpression("^(Mock|Meta)$", ErrorMessage = "Notifications:WhatsApp:Provider must be Mock or Meta.")]
    public string Provider { get; set; } = MockProvider;

    /// <summary>Whether parcel updates go to the receiver's number as well as the sender's.</summary>
    public bool NotifyReceivers { get; set; } = true;

    /// <summary>Meta's id for the sending phone number. Not secret, but specific to your account.</summary>
    public string? PhoneNumberId { get; set; }

    /// <summary>Secret. A system user's access token. Never commit it.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Secret. The app secret that signs Meta's webhook calls.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Secret. The text you type into Meta's webhook setup page; we compare it when Meta verifies the endpoint.</summary>
    public string? VerifyToken { get; set; }

    [Required]
    [RegularExpression(@"^v\d+\.\d+$", ErrorMessage = "Notifications:WhatsApp:ApiVersion looks like v21.0.")]
    public string ApiVersion { get; set; } = "v21.0";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://graph.facebook.com";

    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 15;

    public bool IsMeta => string.Equals(Provider, MetaProvider, StringComparison.Ordinal);
}

public sealed class SmsOptions
{
    public const string MockProvider = "Mock";

    /// <summary>
    /// The name of the SMS provider in use. <c>Mock</c> is built in. A Nepal gateway is added by writing an
    /// <see cref="ISmsProvider"/> and registering it (docs/notifications.md); this is then its name.
    /// </summary>
    [Required]
    [StringLength(50)]
    public string Provider { get; set; } = MockProvider;

    /// <summary>The sender name or number shown on the phone, if the gateway uses one.</summary>
    [StringLength(20)]
    public string? SenderId { get; set; }

    public bool NotifyReceivers { get; set; } = true;
}

public sealed class OutboxOptions
{
    [Range(1, 500)]
    public int BatchSize { get; set; } = 25;

    /// <summary>How long a worker owns a message. If it dies, another worker takes the message after this.</summary>
    [Range(30, 3600)]
    public int LockSeconds { get; set; } = 180;

    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>The wait after the first failure. It doubles with each further failure (with a little random jitter).</summary>
    [Range(1, 3600)]
    public int BaseDelaySeconds { get; set; } = 30;

    [Range(60, 86400)]
    public int MaxDelaySeconds { get; set; } = 3600;

    /// <summary>How long a delivery report that we cannot match yet is kept and retried.</summary>
    [Range(1, 168)]
    public int WebhookRetryHours { get; set; } = 24;
}

/// <summary>Checks that go beyond what attributes can say.</summary>
internal sealed class NotificationOptionsValidator : IValidateOptions<NotificationOptions>
{
    public ValidateOptionsResult Validate(string? name, NotificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.WhatsApp.IsMeta)
        {
            Require(failures, options.WhatsApp.PhoneNumberId, "Notifications:WhatsApp:PhoneNumberId");
            Require(failures, options.WhatsApp.AccessToken, "Notifications:WhatsApp:AccessToken");
            Require(failures, options.WhatsApp.AppSecret, "Notifications:WhatsApp:AppSecret");
            Require(failures, options.WhatsApp.VerifyToken, "Notifications:WhatsApp:VerifyToken");
        }

        if (options.Outbox.MaxDelaySeconds < options.Outbox.BaseDelaySeconds)
        {
            failures.Add("Notifications:Outbox:MaxDelaySeconds must not be smaller than BaseDelaySeconds.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(List<string> failures, string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{key} is required when Notifications:WhatsApp:Provider is Meta. Set it with 'dotnet user-secrets' or an environment variable.");
        }
    }
}

// ---- providers -------------------------------------------------------------------------------------------------------

/// <summary>
/// One message ready to hand to a provider. <paramref name="To"/> is the number with the country code and no plus
/// (9779841234567). <paramref name="IdempotencyKey"/> is our message id: a provider that supports de-duplication can use it.
/// </summary>
public sealed record OutboundMessage(
    NotificationChannel Channel,
    string To,
    string TemplateKey,
    string Language,
    string Body,
    string? ProviderTemplateName,
    IReadOnlyList<string> Parameters,
    string IdempotencyKey);

public enum SendOutcome
{
    /// <summary>The provider took the message and gave it an id. It has not been delivered yet.</summary>
    Accepted = 1,

    /// <summary>The provider could not take it now (down, busy, throttling, network). Try again later.</summary>
    RetryLater = 2,

    /// <summary>The provider refused it for a reason that will not change (bad number, no such template, bad credentials).</summary>
    Rejected = 3,
}

public sealed record SendResult(SendOutcome Outcome, string? ProviderMessageId, string? Error)
{
    public static SendResult Accepted(string providerMessageId) => new(SendOutcome.Accepted, providerMessageId, null);

    public static SendResult RetryLater(string error) => new(SendOutcome.RetryLater, null, error);

    public static SendResult Rejected(string error) => new(SendOutcome.Rejected, null, error);
}

/// <summary>
/// One way of sending messages on one channel. Sending is the only thing it does: whether the message arrived is learned later,
/// from the provider's delivery reports, never from <see cref="SendAsync"/> returning.
/// </summary>
public interface IMessageProvider
{
    NotificationChannel Channel { get; }

    /// <summary>The name stored on messages: for example <c>MetaWhatsApp</c>, <c>MockSms</c>.</summary>
    string Name { get; }

    /// <summary>The name used in configuration: <c>Meta</c>, <c>Mock</c>, or a gateway's own name.</summary>
    string ConfigurationName { get; }

    bool IsMock { get; }

    /// <summary>Whether the credentials this provider needs are present.</summary>
    bool IsConfigured { get; }

    string Description { get; }

    /// <summary>Must not throw for provider problems: return <see cref="SendOutcome.RetryLater"/> or <see cref="SendOutcome.Rejected"/>.</summary>
    Task<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default);
}

/// <summary>A gateway that sends text messages (Nepal SMS gateways implement this).</summary>
public interface ISmsProvider : IMessageProvider
{
}

/// <summary>A provider of WhatsApp template messages.</summary>
public interface IWhatsAppProvider : IMessageProvider
{
}

public interface IMessageProviderRegistry
{
    /// <summary>The provider chosen in configuration for the channel; throws when the configured name is not registered.</summary>
    IMessageProvider Get(NotificationChannel channel);

    IReadOnlyList<NotificationProviderInfo> Describe();

    /// <summary>True when either channel is using a mock provider.</summary>
    bool IsMockInUse { get; }
}

// ---- repositories ----------------------------------------------------------------------------------------------------

public sealed record NotificationFilter(
    NotificationChannel? Channel = null,
    OutboxStatus? Status = null,
    DeliveryStatus? DeliveryStatus = null,
    NotificationAudience? Audience = null,
    string? TemplateKey = null,
    Guid? ParcelId = null,
    string? TrackingPrefix = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null);

public sealed record NotificationRow(NotificationOutboxMessage Message, string? TrackingNumber);

public sealed record ChannelCounts(
    NotificationChannel Channel,
    int Total,
    int Queued,
    int Retrying,
    int Submitted,
    int Sent,
    int Delivered,
    int Read,
    int Failed,
    int Skipped);

public interface INotificationOutboxRepository
{
    void Add(NotificationOutboxMessage message);

    void Add(NotificationLog log);

    /// <summary>The ids of messages that are due now, oldest first (including ones a dead worker left locked).</summary>
    Task<IReadOnlyList<Guid>> GetDueIdsAsync(DateTimeOffset nowUtc, int take, CancellationToken cancellationToken = default);

    /// <summary>Tracked, so it can be changed.</summary>
    Task<NotificationOutboxMessage?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Tracked: the message a delivery report is about.</summary>
    Task<NotificationOutboxMessage?> GetByProviderMessageIdAsync(string provider, string providerMessageId, CancellationToken cancellationToken = default);

    Task<NotificationRow?> GetRowAsync(Guid id, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<NotificationRow> Items, int TotalCount)> SearchAsync(NotificationFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationRow>> ListForParcelAsync(Guid parcelId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationLog>> GetLogsAsync(Guid outboxMessageId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChannelCounts>> CountByChannelAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);

    Task<int> CountDeadLettersAsync(CancellationToken cancellationToken = default);
}

public interface INotificationTemplateRepository
{
    Task<IReadOnlyList<NotificationTemplate>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Tracked.</summary>
    Task<NotificationTemplate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The active templates for an event, one per channel.</summary>
    Task<IReadOnlyList<NotificationTemplate>> FindActiveAsync(string key, string language, CancellationToken cancellationToken = default);

    Task<NotificationTemplate?> FindActiveAsync(string key, NotificationChannel channel, string language, CancellationToken cancellationToken = default);
}

public interface INotificationOptOutRepository
{
    /// <summary>The opt-outs in force for any of the numbers.</summary>
    Task<IReadOnlyList<NotificationOptOut>> GetActiveAsync(IReadOnlyCollection<string> phoneNumbers, CancellationToken cancellationToken = default);

    /// <summary>Tracked.</summary>
    Task<NotificationOptOut?> FindActiveAsync(string phoneNumber, NotificationChannel channel, CancellationToken cancellationToken = default);

    /// <summary>Tracked.</summary>
    Task<NotificationOptOut?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<NotificationOptOut> Items, int TotalCount)> SearchAsync(NotificationChannel? channel, bool activeOnly, int page, int pageSize, CancellationToken cancellationToken = default);

    void Add(NotificationOptOut optOut);
}

public interface INotificationConsentRepository
{
    /// <summary>Every SMS and WhatsApp consent decision the customer has made, newest first.</summary>
    Task<IReadOnlyList<CustomerConsent>> GetChannelConsentsAsync(Guid customerId, CancellationToken cancellationToken = default);

    void Add(CustomerConsent consent);
}

public interface IWebhookEventRepository
{
    Task<bool> ExistsAsync(string provider, string eventId, CancellationToken cancellationToken = default);

    void Add(WebhookEvent webhookEvent);

    /// <summary>Tracked: events that could not be applied yet, oldest first.</summary>
    Task<IReadOnlyList<WebhookEvent>> GetPendingAsync(string provider, DateTimeOffset receivedAfterUtc, int take, CancellationToken cancellationToken = default);
}

public interface IMockProviderMessageRepository
{
    void Add(MockProviderMessage message);

    Task<(IReadOnlyList<MockProviderMessage> Items, int TotalCount)> SearchAsync(NotificationChannel? channel, int page, int pageSize, CancellationToken cancellationToken = default);
}

// ---- services --------------------------------------------------------------------------------------------------------

/// <summary>
/// Decides, inside the business transaction that changed a parcel, which notifications that change calls for and writes them
/// to the outbox. It sends nothing: the outbox row and the status change are saved together or not at all.
/// </summary>
public interface INotificationPlanner
{
    /// <summary>
    /// Stages outbox rows for the status change. Saving is the caller's job.
    /// </summary>
    /// <param name="parcel">The parcel that changed.</param>
    /// <param name="history">The history row of the change; its id makes the notification unique.</param>
    /// <param name="knownSenderConsents">
    /// The sender's consent records when the caller has just created some that are not saved yet (a new booking). Null means
    /// read them from the database.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task PlanAsync(Parcel parcel, ParcelStatusHistory history, IReadOnlyCollection<CustomerConsent>? knownSenderConsents, CancellationToken cancellationToken = default);
}

public sealed record OutboxRunResult(int Claimed, int Submitted, int Retrying, int Failed, int Skipped);

/// <summary>
/// Asks the background worker to look at the outbox soon. Called after a save that queued a message has committed. It is a
/// hint only: the worker also sweeps on a schedule, so a lost signal delays a message and never loses one.
/// </summary>
public interface IOutboxDispatchSignal
{
    void Signal();
}

/// <summary>Sends what is due in the outbox. Called by the background job (Hangfire).</summary>
public interface IOutboxProcessor
{
    Task<OutboxRunResult> ProcessDueAsync(CancellationToken cancellationToken = default);
}

public interface INotificationWebhookService
{
    /// <summary>
    /// A call from Meta. The body is the raw bytes Meta sent and <paramref name="signature"/> the <c>X-Hub-Signature-256</c>
    /// header: the signature is checked against exactly these bytes before anything is read.
    /// </summary>
    Task<WebhookAcceptedResponse> HandleMetaAsync(byte[] body, string? signature, CancellationToken cancellationToken = default);

    /// <summary>The answer to Meta's endpoint check: the challenge to echo back, or null when the token does not match.</summary>
    string? VerifyMetaSubscription(string? mode, string? verifyToken, string? challenge);

    /// <summary>A delivery report from the mock providers (development and test only).</summary>
    Task<WebhookAcceptedResponse> HandleMockAsync(MockWebhookRequest request, CancellationToken cancellationToken = default);

    /// <summary>Tries again the reports that arrived before their message could be matched. Returns how many were applied.</summary>
    Task<int> RetryPendingAsync(CancellationToken cancellationToken = default);
}

public interface INotificationService
{
    Task<PagedResponse<NotificationMessageResponse>> SearchAsync(NotificationMessageQuery query, CancellationToken cancellationToken = default);

    Task<NotificationMessageDetailResponse> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationMessageResponse>> ListForParcelAsync(Guid parcelId, CancellationToken cancellationToken = default);

    Task<NotificationSummaryResponse> SummaryAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);

    /// <summary>Gives a message that failed for good another full set of attempts.</summary>
    Task<NotificationMessageResponse> RequeueAsync(Guid id, RequeueNotificationRequest? request, CancellationToken cancellationToken = default);

    Task<NotificationMessageResponse> CancelAsync(Guid id, RequeueNotificationRequest? request, CancellationToken cancellationToken = default);

    Task<NotificationTemplatesResponse> ListTemplatesAsync(CancellationToken cancellationToken = default);

    Task<NotificationTemplateResponse> UpdateTemplateAsync(Guid id, UpdateNotificationTemplateRequest request, CancellationToken cancellationToken = default);

    Task<PreviewTemplateResponse> PreviewTemplateAsync(PreviewTemplateRequest request, CancellationToken cancellationToken = default);

    Task<PagedResponse<OptOutResponse>> SearchOptOutsAsync(OptOutQuery query, CancellationToken cancellationToken = default);

    Task<OptOutResponse> CreateOptOutAsync(CreateOptOutRequest request, CancellationToken cancellationToken = default);

    Task<OptOutResponse> RemoveOptOutAsync(Guid id, CancellationToken cancellationToken = default);

    IReadOnlyList<NotificationProviderInfo> Providers();

    Task<PagedResponse<MockProviderMessageResponse>> MockInboxAsync(string? channel, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Makes the mock provider report a status for a message (found by the provider's id), going through the same webhook path as a real report.</summary>
    Task<WebhookAcceptedResponse> SimulateStatusAsync(string providerMessageId, SimulateStatusRequest request, CancellationToken cancellationToken = default);
}

public interface INotificationPreferenceService
{
    /// <summary>The signed-in customer's own choices.</summary>
    Task<NotificationPreferencesResponse> GetMineAsync(CancellationToken cancellationToken = default);

    Task<NotificationPreferencesResponse> UpdateMineAsync(UpdateNotificationPreferencesRequest request, CancellationToken cancellationToken = default);

    /// <summary>Counter staff, reading and recording what the sender of a parcel said at the desk.</summary>
    Task<NotificationPreferencesResponse> GetForParcelSenderAsync(Guid parcelId, CancellationToken cancellationToken = default);

    Task<NotificationPreferencesResponse> UpdateForParcelSenderAsync(Guid parcelId, UpdateNotificationPreferencesRequest request, CancellationToken cancellationToken = default);
}
