using System.Text.Json;
using Microsoft.Extensions.Options;
using NepalParcel.Contracts.Notifications;
using NepalParcel.Domain.Common;
using NepalParcel.Domain.Notifications;

namespace NepalParcel.Application.Notifications;

/// <summary>
/// A <b>mock</b> WhatsApp provider. Nothing leaves this application: the message is written to a table that an administrator can
/// read on screen (the "test inbox"), and the provider gives it an id as a real one would. Delivery is not reported by itself:
/// a tester makes it report through the simulate-status endpoint, which goes through the same webhook code a real report does.
/// </summary>
internal sealed class MockWhatsAppProvider(IMockProviderMessageRepository inbox, TimeProvider clock) : MockProviderBase(inbox, clock), IWhatsAppProvider
{
    public override NotificationChannel Channel => NotificationChannel.WhatsApp;

    public override string Name => "MockWhatsApp";
}

/// <summary>A mock SMS gateway, with the same test inbox.</summary>
internal sealed class MockSmsProvider(IMockProviderMessageRepository inbox, TimeProvider clock) : MockProviderBase(inbox, clock), ISmsProvider
{
    public override NotificationChannel Channel => NotificationChannel.Sms;

    public override string Name => "MockSms";
}

internal abstract class MockProviderBase(IMockProviderMessageRepository inbox, TimeProvider clock) : IMessageProvider
{
    public abstract NotificationChannel Channel { get; }

    public abstract string Name { get; }

    public string ConfigurationName => WhatsAppOptions.MockProvider;

    public bool IsMock => true;

    public bool IsConfigured => true;

    public string Description => "Mock provider: messages are kept in the test inbox and nothing is sent to any phone.";

    public Task<SendResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var providerMessageId = $"mock-{(Channel == NotificationChannel.WhatsApp ? "wa" : "sms")}-{Guid.NewGuid():N}";
        inbox.Add(MockProviderMessage.Create(
            Channel,
            providerMessageId,
            message.To,
            message.Body,
            message.ProviderTemplateName,
            JsonSerializer.Serialize(message.Parameters),
            clock.GetUtcNow()));
        return Task.FromResult(SendResult.Accepted(providerMessageId));
    }
}

/// <summary>Finds the provider chosen in configuration for each channel.</summary>
internal sealed class MessageProviderRegistry(IEnumerable<IMessageProvider> providers, IOptions<NotificationOptions> options) : IMessageProviderRegistry
{
    private readonly IReadOnlyList<IMessageProvider> _providers = [.. providers];

    public bool IsMockInUse =>
        TryGet(NotificationChannel.WhatsApp)?.IsMock == true || TryGet(NotificationChannel.Sms)?.IsMock == true;

    public IMessageProvider Get(NotificationChannel channel) =>
        TryGet(channel) ?? throw new DomainException(
            $"No {channel} provider named '{ConfiguredName(channel)}' is registered. Check Notifications:{(channel == NotificationChannel.WhatsApp ? "WhatsApp" : "Sms")}:Provider.");

    public IReadOnlyList<NotificationProviderInfo> Describe() =>
        [.. new[] { NotificationChannel.Sms, NotificationChannel.WhatsApp }.Select(Describe)];

    private NotificationProviderInfo Describe(NotificationChannel channel)
    {
        var provider = TryGet(channel);
        return provider is null
            ? new NotificationProviderInfo(
                channel.ToString(),
                ConfiguredName(channel),
                false,
                false,
                $"No provider named '{ConfiguredName(channel)}' is registered, so {channel} messages cannot be sent.")
            : new NotificationProviderInfo(
                channel.ToString(),
                provider.Name,
                provider.IsMock,
                provider.IsConfigured,
                provider.IsConfigured ? provider.Description : $"{provider.Description} Its credentials are missing.");
    }

    private IMessageProvider? TryGet(NotificationChannel channel) =>
        _providers.FirstOrDefault(p => p.Channel == channel && string.Equals(p.ConfigurationName, ConfiguredName(channel), StringComparison.OrdinalIgnoreCase));

    private string ConfiguredName(NotificationChannel channel) => channel switch
    {
        NotificationChannel.WhatsApp => options.Value.WhatsApp.Provider,
        NotificationChannel.Sms => options.Value.Sms.Provider,
        _ => throw new DomainException($"There are no providers for {channel}."),
    };
}
