using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Notifications;
using NepalParcel.Contracts.Notifications;
using NepalParcel.Domain.Integrations;
using NepalParcel.Domain.Notifications;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

/// <summary>
/// What the server does with a provider's reports: the signature is checked over the exact bytes first, each report is stored
/// once, a repeat changes nothing, a report that cannot be matched yet is kept for later, and a delivered status comes only from
/// a report.
/// </summary>
public sealed class NotificationWebhookTests
{
    private const string Secret = "unit-test-app-secret";
    private const string VerifyToken = "unit-test-verify-token";
    private static readonly DateTimeOffset Now = TestData.Now;

    private readonly IWebhookEventRepository _events = Substitute.For<IWebhookEventRepository>();
    private readonly INotificationOutboxRepository _outbox = Substitute.For<INotificationOutboxRepository>();
    private readonly INotificationOptOutRepository _optOuts = Substitute.For<INotificationOptOutRepository>();
    private readonly IMessageProviderRegistry _providers = Substitute.For<IMessageProviderRegistry>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly NotificationOptions _options = new() { WhatsApp = { AppSecret = Secret, VerifyToken = VerifyToken } };
    private readonly List<WebhookEvent> _stored = [];
    private readonly List<NotificationLog> _logs = [];

    public NotificationWebhookTests()
    {
        _events.When(e => e.Add(Arg.Any<WebhookEvent>())).Do(c => _stored.Add(c.Arg<WebhookEvent>()));
        _outbox.When(o => o.Add(Arg.Any<NotificationLog>())).Do(c => _logs.Add(c.Arg<NotificationLog>()));
        _events.ExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _providers.IsMockInUse.Returns(true);
    }

    private NotificationWebhookService Service() =>
        new(_events, _outbox, _optOuts, _providers, _unitOfWork, Options.Create(_options), new FixedClock(Now), NullLogger<NotificationWebhookService>.Instance);

    private static NotificationOutboxMessage SubmittedMessage(string providerMessageId = "wamid.ABC", string provider = "MetaWhatsApp")
    {
        var message = NotificationOutboxMessage.EnqueueForParcel(
            NotificationChannel.WhatsApp, NotificationAudience.Receiver, "9851234567", NotificationTemplateKeys.ParcelShipped, "{}", Now,
            Guid.NewGuid(), null, Guid.NewGuid().ToString("N"), 5);
        message.BeginProcessing(Now, TimeSpan.FromMinutes(3));
        message.MarkSubmitted(Now, provider, providerMessageId, "text");
        return message;
    }

    private static string StatusBody(string id, string status, long timestamp = 1_795_000_000, string? errors = null) =>
        $$$"""
        {"object":"whatsapp_business_account","entry":[{"id":"1","changes":[{"field":"messages","value":{"messaging_product":"whatsapp","statuses":[{"id":"{{{id}}}","status":"{{{status}}}","timestamp":"{{{timestamp}}}","recipient_id":"9779851234567"{{{(errors is null ? string.Empty : "," + errors)}}}}]}}]}]}
        """;

    private static string InboundBody(string id, string from, string text) =>
        $$$"""
        {"object":"whatsapp_business_account","entry":[{"id":"1","changes":[{"field":"messages","value":{"messaging_product":"whatsapp","messages":[{"from":"{{{from}}}","id":"{{{id}}}","timestamp":"1795000000","type":"text","text":{"body":"{{{text}}}"}}]}}]}]}
        """;

    private static string Sign(string body, string secret = Secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private Task<WebhookAcceptedResponse> PostAsync(string body, string? signature = null) =>
        Service().HandleMetaAsync(Encoding.UTF8.GetBytes(body), signature ?? Sign(body));

    // ---- signature ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSignature_IsAnHmacOfTheExactBody_KeyedWithTheAppSecret()
    {
        var body = Encoding.UTF8.GetBytes("{\"a\":1}");

        Assert.True(NotificationWebhookService.SignatureIsValid(body, Sign("{\"a\":1}"), Secret));
        Assert.True(NotificationWebhookService.SignatureIsValid(body, Sign("{\"a\":1}").ToUpperInvariant().Replace("SHA256=", "sha256=", StringComparison.Ordinal), Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("sha256=nothex")]
    [InlineData("md5=abcdef")]
    [InlineData("sha256=00")]
    public void AMissingMalformedOrWrongSignature_IsRefused(string? header)
    {
        Assert.False(NotificationWebhookService.SignatureIsValid(Encoding.UTF8.GetBytes("{\"a\":1}"), header, Secret));
    }

    [Fact]
    public void ABodyThatWasChangedAfterSigning_IsRefused()
    {
        var signature = Sign("{\"a\":1}");

        Assert.False(NotificationWebhookService.SignatureIsValid(Encoding.UTF8.GetBytes("{\"a\":2}"), signature, Secret));
        Assert.False(NotificationWebhookService.SignatureIsValid(Encoding.UTF8.GetBytes("{\"a\":1}"), signature, "another-secret"));
    }

    [Fact]
    public void WithNoAppSecretConfigured_NothingCanBeVerified_SoNothingIsAccepted()
    {
        Assert.False(NotificationWebhookService.SignatureIsValid(Encoding.UTF8.GetBytes("{}"), Sign("{}", ""), null));
        Assert.False(NotificationWebhookService.SignatureIsValid(Encoding.UTF8.GetBytes("{}"), Sign("{}", ""), ""));
    }

    [Fact]
    public async Task ABadSignature_IsRefused_AndNothingIsStoredOrChanged()
    {
        var body = StatusBody("wamid.ABC", "delivered");

        await Assert.ThrowsAsync<UnauthorizedException>(() => PostAsync(body, Sign(body, "wrong")));
        await Assert.ThrowsAsync<UnauthorizedException>(() => PostAsync(body, "sha256=00"));

        Assert.Empty(_stored);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _outbox.DidNotReceive().GetByProviderMessageIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithNoSecretConfigured_EvenAValidLookingCallIsRefused()
    {
        _options.WhatsApp.AppSecret = null;
        var body = StatusBody("wamid.ABC", "delivered");

        await Assert.ThrowsAsync<UnauthorizedException>(() => PostAsync(body));
    }

    // ---- the endpoint check ------------------------------------------------------------------------------------------

    [Fact]
    public void MetasEndpointCheck_IsAnsweredOnlyWhenTheTokenMatches()
    {
        var service = Service();

        Assert.Equal("1158201444", service.VerifyMetaSubscription("subscribe", VerifyToken, "1158201444"));
        Assert.Null(service.VerifyMetaSubscription("subscribe", "wrong", "1158201444"));
        Assert.Null(service.VerifyMetaSubscription("unsubscribe", VerifyToken, "1158201444"));
        Assert.Null(service.VerifyMetaSubscription("subscribe", VerifyToken, null));
        Assert.Null(service.VerifyMetaSubscription("subscribe", null, "1158201444"));
    }

    [Fact]
    public void WithNoVerifyTokenConfigured_TheEndpointCheckIsNeverAnswered()
    {
        _options.WhatsApp.VerifyToken = null;

        Assert.Null(Service().VerifyMetaSubscription("subscribe", string.Empty, "x"));
        Assert.Null(Service().VerifyMetaSubscription("subscribe", null, "x"));
    }

    // ---- applying reports --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeliveredReport_MarksTheMessageDelivered_RecordsIt_AndIsStoredAsProcessed()
    {
        var message = SubmittedMessage();
        _outbox.GetByProviderMessageIdAsync("MetaWhatsApp", "wamid.ABC", Arg.Any<CancellationToken>()).Returns(message);

        var result = await PostAsync(StatusBody("wamid.ABC", "delivered"));

        Assert.Equal(new WebhookAcceptedResponse(1, 1, 0, 0), result);
        Assert.Equal(DeliveryStatus.Delivered, message.DeliveryStatus);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_795_000_000), message.DeliveredAtUtc);
        Assert.Equal(OutboxStatus.Submitted, message.Status);
        var stored = Assert.Single(_stored);
        Assert.Equal(WebhookStatus.Processed, stored.Status);
        Assert.Equal("wamid.ABC:delivered", stored.EventId);
        Assert.True(stored.SignatureValid);
        var log = Assert.Single(_logs);
        Assert.Equal(NotificationLogStatus.Delivered, log.Status);
        Assert.Equal(message.Id, log.OutboxMessageId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_795_000_000), log.OccurredAtUtc);
        Assert.Equal("98******67", log.RecipientMasked);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheStoredReport_ContainsNoPhoneNumber()
    {
        _outbox.GetByProviderMessageIdAsync("MetaWhatsApp", "wamid.ABC", Arg.Any<CancellationToken>()).Returns(SubmittedMessage());

        await PostAsync(StatusBody("wamid.ABC", "delivered"));

        var stored = Assert.Single(_stored);
        Assert.DoesNotContain("9779851234567", stored.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("9851234567", stored.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepeatedReport_IsAcknowledgedAndChangesNothing()
    {
        _events.ExistsAsync("MetaWhatsApp", "wamid.ABC:delivered", Arg.Any<CancellationToken>()).Returns(true);

        var result = await PostAsync(StatusBody("wamid.ABC", "delivered"));

        Assert.Equal(new WebhookAcceptedResponse(1, 0, 1, 0), result);
        Assert.Empty(_stored);
        Assert.Empty(_logs);
        await _outbox.DidNotReceive().GetByProviderMessageIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneCallCarryingSeveralReports_AppliesEachOnce()
    {
        var message = SubmittedMessage();
        _outbox.GetByProviderMessageIdAsync("MetaWhatsApp", "wamid.ABC", Arg.Any<CancellationToken>()).Returns(message);
        _events.ExistsAsync("MetaWhatsApp", "wamid.ABC:sent", Arg.Any<CancellationToken>()).Returns(true);
        var body = """
        {"entry":[{"changes":[{"value":{"statuses":[
          {"id":"wamid.ABC","status":"sent","timestamp":"1795000000"},
          {"id":"wamid.ABC","status":"delivered","timestamp":"1795000005"},
          {"id":"wamid.ABC","status":"read","timestamp":"1795000090"}]}}]}]}
        """;

        var result = await PostAsync(body);

        Assert.Equal(new WebhookAcceptedResponse(3, 2, 1, 0), result);
        Assert.Equal(DeliveryStatus.Read, message.DeliveryStatus);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_795_000_005), message.DeliveredAtUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_795_000_090), message.ReadAtUtc);
    }

    [Fact]
    public async Task AReportForAMessageWeCannotFindYet_IsKeptAndNotLost()
    {
        _outbox.GetByProviderMessageIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((NotificationOutboxMessage?)null);

        var result = await PostAsync(StatusBody("wamid.NEW", "delivered"));

        Assert.Equal(new WebhookAcceptedResponse(1, 0, 0, 1), result);
        var stored = Assert.Single(_stored);
        Assert.Equal(WebhookStatus.Received, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Empty(_logs);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AReportForAMessageNotYetSubmitted_IsKeptToo()
    {
        var processing = NotificationOutboxMessage.EnqueueForParcel(
            NotificationChannel.WhatsApp, NotificationAudience.Receiver, "9851234567", NotificationTemplateKeys.ParcelShipped, "{}", Now,
            Guid.NewGuid(), null, "k", 5);
        processing.BeginProcessing(Now, TimeSpan.FromMinutes(3));
        _outbox.GetByProviderMessageIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(processing);

        var result = await PostAsync(StatusBody("wamid.ABC", "delivered"));

        Assert.Equal(1, result.Pending);
        Assert.Equal(DeliveryStatus.None, processing.DeliveryStatus);
    }

    [Fact]
    public async Task APendingReport_IsAppliedOnceItsMessageAppears()
    {
        _outbox.GetByProviderMessageIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((NotificationOutboxMessage?)null);
        await PostAsync(StatusBody("wamid.ABC", "delivered"));
        var pending = Assert.Single(_stored);
        _events.GetPendingAsync("MetaWhatsApp", Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([pending]);
        _events.GetPendingAsync("Mock", Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        var message = SubmittedMessage();
        _outbox.GetByProviderMessageIdAsync("MetaWhatsApp", "wamid.ABC", Arg.Any<CancellationToken>()).Returns(message);

        var applied = await Service().RetryPendingAsync();

        Assert.Equal(1, applied);
        Assert.Equal(DeliveryStatus.Delivered, message.DeliveryStatus);
        Assert.Equal(WebhookStatus.Processed, pending.Status);
        Assert.Equal(2, pending.AttemptCount);
    }

    [Fact]
    public async Task AFailedReport_RecordsMetasReason_AndDoesNotPretendItWasDelivered()
    {
        var message = SubmittedMessage();
        _outbox.GetByProviderMessageIdAsync("MetaWhatsApp", "wamid.ABC", Arg.Any<CancellationToken>()).Returns(message);

        await PostAsync(StatusBody("wamid.ABC", "failed", errors: "\"errors\":[{\"code\":131026,\"title\":\"Message undeliverable\"}]"));

        Assert.Equal(DeliveryStatus.Failed, message.DeliveryStatus);
        Assert.Null(message.DeliveredAtUtc);
        Assert.Contains("131026", message.DeliveryError, StringComparison.Ordinal);
        Assert.Contains("Message undeliverable", message.DeliveryError, StringComparison.Ordinal);
        Assert.Equal(NotificationLogStatus.Failed, Assert.Single(_logs).Status);
    }

    [Fact]
    public async Task AReportOfAStateWeDoNotTrack_IsHandledWithoutChangingTheMessage()
    {
        var message = SubmittedMessage();
        _outbox.GetByProviderMessageIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(message);

        var result = await PostAsync(StatusBody("wamid.ABC", "deleted"));

        Assert.Equal(1, result.Applied);
        Assert.Equal(DeliveryStatus.None, message.DeliveryStatus);
        Assert.Empty(_logs);
        Assert.Equal(WebhookStatus.Processed, Assert.Single(_stored).Status);
    }

    [Fact]
    public async Task ALateSentReport_DoesNotMoveAMessageBackwards()
    {
        var message = SubmittedMessage();
        message.ApplyProviderStatus(DeliveryStatus.Delivered, Now);
        _outbox.GetByProviderMessageIdAsync("MetaWhatsApp", "wamid.ABC", Arg.Any<CancellationToken>()).Returns(message);

        await PostAsync(StatusBody("wamid.ABC", "sent"));

        Assert.Equal(DeliveryStatus.Delivered, message.DeliveryStatus);
        Assert.Null(message.SentAtUtc);
    }

    [Fact]
    public async Task TheMessageIsFoundByTheProvidersId_NotByAnythingTheCallerChooses()
    {
        _outbox.GetByProviderMessageIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((NotificationOutboxMessage?)null);

        await PostAsync(StatusBody("wamid.ABC", "delivered"));

        await _outbox.Received(1).GetByProviderMessageIdAsync("MetaWhatsApp", "wamid.ABC", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ABodyThatIsNotJson_IsRejectedAsABadRequest_AfterTheSignatureChecksOut()
    {
        var body = "this is not json";

        await Assert.ThrowsAsync<RequestValidationException>(() => PostAsync(body));
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task ACallWithNothingWeUnderstand_IsAcceptedAndIgnored()
    {
        var result = await PostAsync("{\"object\":\"whatsapp_business_account\",\"entry\":[]}");

        Assert.Equal(new WebhookAcceptedResponse(0, 0, 0, 0), result);
    }

    // ---- STOP and START ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("STOP")]
    [InlineData("stop")]
    [InlineData("  Unsubscribe ")]
    public async Task ReplyingStop_OptsTheNumberOutOfWhatsApp(string text)
    {
        NotificationOptOut? created = null;
        _optOuts.FindActiveAsync("9851234567", NotificationChannel.WhatsApp, Arg.Any<CancellationToken>()).Returns((NotificationOptOut?)null);
        _optOuts.When(o => o.Add(Arg.Any<NotificationOptOut>())).Do(c => created = c.Arg<NotificationOptOut>());

        var result = await PostAsync(InboundBody("wamid.IN1", "9779851234567", text));

        Assert.Equal(1, result.Applied);
        Assert.NotNull(created);
        Assert.Equal("9851234567", created.PhoneNumber);
        Assert.Equal(NotificationChannel.WhatsApp, created.Channel);
        Assert.Equal(OptOutSource.InboundMessage, created.Source);
        var stored = Assert.Single(_stored);
        Assert.Equal("{}", stored.PayloadJson);
        Assert.Equal(WebhookStatus.Processed, stored.Status);
    }

    [Fact]
    public async Task ReplyingStopAgain_DoesNotCreateASecondOptOut()
    {
        _optOuts.FindActiveAsync("9851234567", NotificationChannel.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(NotificationOptOut.Create("9851234567", NotificationChannel.WhatsApp, OptOutSource.InboundMessage, Now));

        await PostAsync(InboundBody("wamid.IN2", "9779851234567", "STOP"));

        _optOuts.DidNotReceive().Add(Arg.Any<NotificationOptOut>());
    }

    [Fact]
    public async Task ReplyingStart_LiftsTheOptOut()
    {
        var existing = NotificationOptOut.Create("9851234567", NotificationChannel.WhatsApp, OptOutSource.InboundMessage, Now);
        _optOuts.FindActiveAsync("9851234567", NotificationChannel.WhatsApp, Arg.Any<CancellationToken>()).Returns(existing);

        await PostAsync(InboundBody("wamid.IN3", "9779851234567", "START"));

        Assert.False(existing.IsActive);
    }

    [Fact]
    public async Task AnyOtherReply_ChangesNothingAndIsNotKept()
    {
        var result = await PostAsync(InboundBody("wamid.IN4", "9779851234567", "Thanks, got it!"));

        Assert.Equal(1, result.Applied);
        _optOuts.DidNotReceive().Add(Arg.Any<NotificationOptOut>());
        await _optOuts.DidNotReceive().FindActiveAsync(Arg.Any<string>(), Arg.Any<NotificationChannel>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain("Thanks", Assert.Single(_stored).PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepeatedReply_IsAcknowledgedOnce()
    {
        _events.ExistsAsync("MetaWhatsApp", "msg:wamid.IN5", Arg.Any<CancellationToken>()).Returns(true);

        var result = await PostAsync(InboundBody("wamid.IN5", "9779851234567", "STOP"));

        Assert.Equal(1, result.Duplicates);
        _optOuts.DidNotReceive().Add(Arg.Any<NotificationOptOut>());
    }

    // ---- the mock ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMockReport_GoesThroughTheSamePath_AndIsAlsoStoredOnce()
    {
        var message = SubmittedMessage("mock-wa-123", "MockWhatsApp");
        _outbox.GetByProviderMessageIdAsync("MockWhatsApp", "mock-wa-123", Arg.Any<CancellationToken>()).Returns(message);

        var first = await Service().HandleMockAsync(new MockWebhookRequest("evt-1", "mock-wa-123", "delivered", Now.AddSeconds(3)));
        _events.ExistsAsync("Mock", "evt-1", Arg.Any<CancellationToken>()).Returns(true);
        var second = await Service().HandleMockAsync(new MockWebhookRequest("evt-1", "mock-wa-123", "delivered", Now.AddSeconds(3)));

        Assert.Equal(1, first.Applied);
        Assert.Equal(1, second.Duplicates);
        Assert.Equal(DeliveryStatus.Delivered, message.DeliveryStatus);
        Assert.Equal(Now.AddSeconds(3), message.DeliveredAtUtc);
    }

    [Fact]
    public async Task AMockSmsReport_FindsTheMessageUnderTheMockSmsProvider()
    {
        var message = SubmittedMessage("mock-sms-9", "MockSms");
        _outbox.GetByProviderMessageIdAsync("MockSms", "mock-sms-9", Arg.Any<CancellationToken>()).Returns(message);

        await Service().HandleMockAsync(new MockWebhookRequest("evt-2", "mock-sms-9", "failed", null, "no signal"));

        Assert.Equal(DeliveryStatus.Failed, message.DeliveryStatus);
        Assert.Equal("no signal", message.DeliveryError);
    }

    [Fact]
    public async Task TheMockWebhook_DoesNotExistWhenARealProviderIsInUse()
    {
        _providers.IsMockInUse.Returns(false);

        await Assert.ThrowsAsync<NotFoundException>(() => Service().HandleMockAsync(new MockWebhookRequest("evt-1", "mock-wa-1", "delivered")));
    }

    [Theory]
    [InlineData("", "m", "delivered")]
    [InlineData("e", "", "delivered")]
    [InlineData("e", "m", "teleported")]
    [InlineData("e", "m", "")]
    public async Task AMockReport_NeedsAnEventIdAMessageIdAndAKnownStatus(string eventId, string messageId, string status)
    {
        await Assert.ThrowsAsync<RequestValidationException>(() => Service().HandleMockAsync(new MockWebhookRequest(eventId, messageId, status)));
    }

    [Theory]
    [InlineData("sent", DeliveryStatus.Sent)]
    [InlineData("DELIVERED", DeliveryStatus.Delivered)]
    [InlineData(" read ", DeliveryStatus.Read)]
    [InlineData("failed", DeliveryStatus.Failed)]
    public void KnownStatuses_AreRecognised(string text, DeliveryStatus expected)
    {
        Assert.Equal(expected, NotificationWebhookService.ParseStatus(text));
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("accepted")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownStatuses_AreNotMistakenForDelivery(string? text)
    {
        Assert.Null(NotificationWebhookService.ParseStatus(text));
    }
}
