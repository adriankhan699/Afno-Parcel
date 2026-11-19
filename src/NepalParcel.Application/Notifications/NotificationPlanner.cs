using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Tracking;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Notifications;
using NepalParcel.Domain.Parcels;

namespace NepalParcel.Application.Notifications;

/// <param name="Audience">Who the message is for.</param>
/// <param name="IsMobileNumber">SMS and WhatsApp reach mobile phones only.</param>
/// <param name="OptedOut">The number has asked not to be messaged on this channel.</param>
/// <param name="SenderConsent">For the sender: the customer's latest decision for this channel, or null if they never made one.</param>
/// <param name="NotifyReceivers">The configuration switch for messaging receivers on this channel.</param>
/// <param name="TemplateAvailable">An active template exists for this message on this channel.</param>
/// <param name="ProviderTemplateNamed">For WhatsApp: the template carries the name Meta approved. Always true for SMS.</param>
public sealed record EligibilityInput(
    NotificationAudience Audience,
    NotificationChannel Channel,
    bool IsMobileNumber,
    bool OptedOut,
    bool? SenderConsent,
    bool NotifyReceivers,
    bool TemplateAvailable,
    bool ProviderTemplateNamed);

public sealed record EligibilityDecision(bool Send, string? Reason)
{
    public static EligibilityDecision Yes { get; } = new(true, null);

    public static EligibilityDecision No(string reason) => new(false, reason);
}

/// <summary>
/// The rules for whether a person is messaged on a channel. In order: the number must be a mobile; an opt-out always wins; the
/// <b>sender</b> must have agreed (silence is not agreement); a <b>receiver</b> is not a customer and has nobody to agree, so
/// they are messaged unless they opted out or the operator switched receivers off; and a template must exist.
/// </summary>
public static class NotificationEligibility
{
    public static EligibilityDecision Decide(EligibilityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var channel = ChannelName(input.Channel);

        if (!input.IsMobileNumber)
        {
            return EligibilityDecision.No($"The number is not a mobile number, so it cannot receive {channel} messages.");
        }

        if (input.OptedOut)
        {
            return EligibilityDecision.No($"The number has opted out of {channel} messages.");
        }

        if (input.Audience == NotificationAudience.Sender)
        {
            if (input.SenderConsent is null)
            {
                return EligibilityDecision.No($"The sender has not agreed to receive {channel} messages.");
            }

            if (input.SenderConsent == false)
            {
                return EligibilityDecision.No($"The sender has withdrawn consent for {channel} messages.");
            }
        }
        else if (!input.NotifyReceivers)
        {
            return EligibilityDecision.No($"{channel} messages to receivers are switched off.");
        }

        if (!input.TemplateAvailable)
        {
            return EligibilityDecision.No($"There is no active {channel} template for this message.");
        }

        if (!input.ProviderTemplateNamed)
        {
            return EligibilityDecision.No("The WhatsApp template has no approved template name yet.");
        }

        return EligibilityDecision.Yes;
    }

    /// <summary>The customer's latest decision for the consent type, or null when they never made one.</summary>
    public static bool? CurrentConsent(IEnumerable<CustomerConsent> consents, ConsentType type)
    {
        ArgumentNullException.ThrowIfNull(consents);
        return consents
            .Where(c => c.Type == type)
            .OrderByDescending(c => c.RecordedAtUtc)
            .ThenByDescending(c => c.Id)
            .Select(c => (bool?)c.IsGranted)
            .FirstOrDefault();
    }

    public static ConsentType ConsentTypeFor(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Sms => ConsentType.SmsNotifications,
        NotificationChannel.WhatsApp => ConsentType.WhatsAppNotifications,
        _ => throw new DomainException($"There is no consent type for {channel}."),
    };

    /// <summary>A Nepali mobile in national form (10 digits, starting 96, 97 or 98).</summary>
    public static bool IsMobile(string? normalizedPhone) =>
        normalizedPhone is { Length: 10 } && NepalPhoneNumber.TryNormalize(normalizedPhone, out var phone) && phone.Length == 10 && phone[0] == '9';

    /// <summary>9841234567 → 9779841234567, the form providers want.</summary>
    public static string ToInternational(string normalizedPhone) => "977" + normalizedPhone;

    private static string ChannelName(NotificationChannel channel) => channel == NotificationChannel.WhatsApp ? "WhatsApp" : "SMS";
}

/// <summary>The wait before another attempt: doubling from a base, capped, with a little jitter so retries do not arrive together.</summary>
public static class RetryBackoff
{
    /// <param name="attemptsMade">How many attempts have been made so far (1 after the first failure).</param>
    /// <param name="jitter">A number from 0 to 1 that moves the delay within ±20%.</param>
    public static TimeSpan Delay(int attemptsMade, OutboxOptions options, double jitter)
    {
        ArgumentNullException.ThrowIfNull(options);
        var exponent = Math.Clamp(attemptsMade - 1, 0, 30);
        var seconds = Math.Min(options.BaseDelaySeconds * Math.Pow(2, exponent), options.MaxDelaySeconds);
        var factor = 0.8 + (0.4 * Math.Clamp(jitter, 0, 1));
        return TimeSpan.FromSeconds(Math.Min(seconds * factor, options.MaxDelaySeconds));
    }

    public static TimeSpan Delay(int attemptsMade, OutboxOptions options) =>
        Delay(attemptsMade, options, RandomNumberGenerator.GetInt32(0, 1001) / 1000.0);
}

internal sealed class NotificationPlanner(
    INotificationOutboxRepository outbox,
    INotificationTemplateRepository templates,
    INotificationOptOutRepository optOuts,
    INotificationConsentRepository consents,
    IOptions<NotificationOptions> options,
    IOptions<TrackingOptions> tracking,
    TimeProvider clock) : INotificationPlanner
{
    private static readonly NotificationChannel[] Channels = [NotificationChannel.Sms, NotificationChannel.WhatsApp];

    public async Task PlanAsync(Parcel parcel, ParcelStatusHistory history, IReadOnlyCollection<CustomerConsent>? knownSenderConsents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        ArgumentNullException.ThrowIfNull(history);

        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        var key = NotificationEvents.TemplateFor((ParcelStatusCode)history.ToStatusId);
        if (key is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var language = NotificationOutboxMessage.DefaultLanguage;
        var senderPhone = Normalize(parcel.SenderPhone);
        var receiverPhone = Normalize(parcel.ReceiverPhone);

        // A person who is both sender and receiver gets one message, as the sender, not two.
        var audiences = new List<(NotificationAudience Audience, string Phone, Guid? CustomerId)> { (NotificationAudience.Sender, senderPhone, parcel.SenderCustomerId) };
        if (!string.Equals(senderPhone, receiverPhone, StringComparison.Ordinal))
        {
            audiences.Add((NotificationAudience.Receiver, receiverPhone, null));
        }

        var activeOptOuts = await optOuts.GetActiveAsync([senderPhone, receiverPhone], cancellationToken).ConfigureAwait(false);
        var senderConsents = knownSenderConsents
            ?? await consents.GetChannelConsentsAsync(parcel.SenderCustomerId, cancellationToken).ConfigureAwait(false);
        var availableTemplates = await templates.FindActiveAsync(key, language, cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["tracking"] = parcel.TrackingNumber,
            ["link"] = $"{tracking.Value.PublicBaseUrl.TrimEnd('/')}/track/{parcel.TrackingNumber}",
            ["service"] = parcel.ServiceType.ToString(),
            ["eta"] = TemplateRenderer.FormatDate(parcel.EstimatedDeliveryAtUtc),
        });
        var dedupeKey = history.Id.ToString("N");

        foreach (var (audience, phone, customerId) in audiences)
        {
            foreach (var channel in Channels)
            {
                var template = availableTemplates.FirstOrDefault(t => t.Channel == channel);
                var input = new EligibilityInput(
                    audience,
                    channel,
                    NotificationEligibility.IsMobile(phone),
                    activeOptOuts.Any(o => o.Channel == channel && string.Equals(o.PhoneNumber, phone, StringComparison.Ordinal)),
                    audience == NotificationAudience.Sender
                        ? NotificationEligibility.CurrentConsent(senderConsents, NotificationEligibility.ConsentTypeFor(channel))
                        : null,
                    channel == NotificationChannel.WhatsApp ? settings.WhatsApp.NotifyReceivers : settings.Sms.NotifyReceivers,
                    template is not null,
                    channel != NotificationChannel.WhatsApp || !string.IsNullOrWhiteSpace(template?.ProviderTemplateName));

                var decision = NotificationEligibility.Decide(input);
                outbox.Add(decision.Send
                    ? NotificationOutboxMessage.EnqueueForParcel(
                        channel, audience, phone, key, payload, now, parcel.Id, customerId, dedupeKey, settings.Outbox.MaxAttempts, language)
                    : NotificationOutboxMessage.CreateSkipped(
                        channel, audience, phone, key, now, decision.Reason!, parcel.Id, customerId, dedupeKey, settings.Outbox.MaxAttempts, language));
            }
        }
    }

    private static string Normalize(string phone) =>
        NepalPhoneNumber.TryNormalize(phone, out var normalized) ? normalized : phone.Trim();
}
