using NepalParcel.Domain.Common;

namespace NepalParcel.Domain.Integrations;

public enum WebhookStatus
{
    Received = 1,
    Processed = 2,
    Failed = 3,
    Ignored = 4,
}

/// <summary>
/// An inbound webhook (payment gateway, SMS delivery report...). The unique (Provider, EventId) pair makes
/// processing idempotent: a replayed event is detected and ignored. The signature is verified before processing.
/// </summary>
public sealed class WebhookEvent : Entity
{
    private WebhookEvent()
    {
        Provider = string.Empty;
        EventId = string.Empty;
        EventType = string.Empty;
        PayloadJson = "{}";
    }

    public string Provider { get; private set; }

    public string EventId { get; private set; }

    public string EventType { get; private set; }

    public string PayloadJson { get; private set; }

    public bool SignatureValid { get; private set; }

    public WebhookStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public string? Error { get; private set; }

    public static WebhookEvent Receive(string provider, string eventId, string eventType, string payloadJson, bool signatureValid, DateTimeOffset receivedAtUtc) =>
        new()
        {
            Provider = Guard.NotBlank(provider, "Provider", 50),
            EventId = Guard.NotBlank(eventId, "Event id", 100),
            EventType = Guard.NotBlank(eventType, "Event type", 100),
            PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            SignatureValid = signatureValid,
            Status = signatureValid ? WebhookStatus.Received : WebhookStatus.Ignored,
            ReceivedAtUtc = receivedAtUtc,
            Error = signatureValid ? null : "Invalid signature",
        };

    public void MarkProcessed(DateTimeOffset atUtc)
    {
        AttemptCount++;
        Status = WebhookStatus.Processed;
        ProcessedAtUtc = atUtc;
        Error = null;
    }

    public void MarkFailed(string error)
    {
        AttemptCount++;
        Status = WebhookStatus.Failed;
        Error = Guard.NotBlank(error, "Error", 1000);
    }
}
