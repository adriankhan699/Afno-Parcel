using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Application.Notifications;
using NepalParcel.Contracts.Notifications;
using NepalParcel.Contracts.Parcels;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Notifications;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;
using static NepalParcel.IntegrationTests.Infrastructure.NotificationTestSupport;

namespace NepalParcel.IntegrationTests;

/// <summary>
/// The notification pipeline from a parcel event to a phone: the outbox row is written with the status change, a worker sends it
/// through a provider, and only the provider's own reports move a message beyond "submitted".
/// </summary>
public sealed class NotificationFlowTests(NotificationApiFactory factory) : IClassFixture<NotificationApiFactory>
{
    private readonly BookingTestHelpers _helpers = new(factory);

    private static Uri MessageUri(Guid id) => new($"/api/v1/notifications/messages/{id}", UriKind.Relative);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>A parcel booked at the counter. Phone numbers are unique per test so opt-outs never leak between tests.</summary>
    private async Task<(ParcelResponse Parcel, Guid Branch, string ReceiverPhone, HttpClient Counter)> BookAsync(bool? sms = true, bool? whatsApp = true, string? receiverPhone = null)
    {
        var branch = await _helpers.CreateBranchAsync();
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);
        var phone = receiverPhone ?? NewPhone();
        var request = NewBooking(branch, senderName: NewName("Sender"), receiverPhone: phone) with { NotifyBySms = sms, NotifyByWhatsApp = whatsApp };
        return (await BookingTestHelpers.BookAsync(counter, request), branch, phone, counter);
    }

    // ---- queueing ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABooking_QueuesAMessageForTheSenderAndTheReceiver_OnBothChannels()
    {
        var (parcel, _, receiverPhone, _) = await BookAsync();

        var messages = await MessagesAsync(factory, parcel.Id, NotificationTemplateKeys.ParcelBooked);

        Assert.Equal(4, messages.Count);
        Assert.All(messages, m => Assert.Equal(OutboxStatus.Pending, m.Status));
        Assert.All(messages, m => Assert.Equal(DeliveryStatus.None, m.DeliveryStatus));
        Assert.Equal(
            ["Receiver:Sms", "Receiver:WhatsApp", "Sender:Sms", "Sender:WhatsApp"],
            messages.Select(m => $"{m.Audience}:{m.Channel}").Order().ToArray());
        Assert.Equal("9841234567", messages.First(m => m.Audience == NotificationAudience.Sender).Recipient);
        Assert.Equal(receiverPhone, messages.First(m => m.Audience == NotificationAudience.Receiver).Recipient);
        Assert.All(messages, m => Assert.NotNull(m.DedupeKey));
    }

    [Fact]
    public async Task ASenderWhoNeverAgreed_IsNotMessaged_AndTheReasonIsRecorded()
    {
        var (parcel, _, _, _) = await BookAsync(sms: null, whatsApp: null);

        var sender = await MessageAsync(factory, parcel.Id, NotificationAudience.Sender, NotificationChannel.Sms);
        var receiver = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms);

        Assert.Equal(OutboxStatus.Skipped, sender.Status);
        Assert.Contains("not agreed", sender.SkipReason, StringComparison.Ordinal);
        Assert.Equal(OutboxStatus.Pending, receiver.Status);
    }

    [Fact]
    public async Task ConsentIsPerChannel_AndRecordedAsAnImmutableHistory()
    {
        var (parcel, _, _, _) = await BookAsync(sms: true, whatsApp: false);

        Assert.Equal(OutboxStatus.Pending, (await MessageAsync(factory, parcel.Id, NotificationAudience.Sender, NotificationChannel.Sms)).Status);
        var whatsApp = await MessageAsync(factory, parcel.Id, NotificationAudience.Sender, NotificationChannel.WhatsApp);
        Assert.Equal(OutboxStatus.Skipped, whatsApp.Status);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var customerId = (await db.Parcels.AsNoTracking().SingleAsync(p => p.Id == parcel.Id)).SenderCustomerId;
        var consents = await db.CustomerConsents.AsNoTracking().Where(c => c.CustomerId == customerId).ToListAsync();
        Assert.Equal(2, consents.Count);
        Assert.True(consents.Single(c => c.Type == NepalParcel.Domain.Customers.ConsentType.SmsNotifications).IsGranted);
        Assert.False(consents.Single(c => c.Type == NepalParcel.Domain.Customers.ConsentType.WhatsAppNotifications).IsGranted);
        Assert.All(consents, c => Assert.Equal(NepalParcel.Domain.Customers.ConsentSource.Counter, c.Source));
    }

    [Fact]
    public async Task ANumberThatOptedOutBeforeTheBooking_IsSkippedOnThatChannelOnly()
    {
        var phone = NewPhone();
        var admin = await AdminAsync(factory);
        (await admin.PostAsJsonAsync(new Uri("/api/v1/notifications/opt-outs", UriKind.Relative), new CreateOptOutRequest(phone, "Sms"))).EnsureSuccessStatusCode();

        var (parcel, _, _, _) = await BookAsync(receiverPhone: phone);

        var sms = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms);
        Assert.Equal(OutboxStatus.Skipped, sms.Status);
        Assert.Contains("opted out", sms.SkipReason, StringComparison.Ordinal);
        Assert.Equal(OutboxStatus.Pending, (await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.WhatsApp)).Status);
    }

    // ---- sending: accepted is not delivered --------------------------------------------------------------------------

    [Fact]
    public async Task TheWorker_SubmitsMessagesToTheProvider_ButNeverMarksThemDelivered()
    {
        var (parcel, _, _, _) = await BookAsync();

        var run = await ProcessAllAsync(factory);

        Assert.True(run.Submitted >= 4);
        var sms = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms);
        Assert.Equal(OutboxStatus.Submitted, sms.Status);
        Assert.Equal(DeliveryStatus.None, sms.DeliveryStatus);
        Assert.Null(sms.SentAtUtc);
        Assert.Null(sms.DeliveredAtUtc);
        Assert.Equal("MockSms", sms.Provider);
        Assert.StartsWith("mock-sms-", sms.ProviderMessageId, StringComparison.Ordinal);
        Assert.Equal(1, sms.AttemptCount);
        Assert.NotNull(sms.SubmittedAtUtc);
        Assert.Contains(parcel.TrackingNumber, sms.RenderedBody, StringComparison.Ordinal);
        Assert.Contains($"/track/{parcel.TrackingNumber}", sms.RenderedBody, StringComparison.Ordinal);

        var whatsApp = await MessageAsync(factory, parcel.Id, NotificationAudience.Sender, NotificationChannel.WhatsApp);
        Assert.Equal("MockWhatsApp", whatsApp.Provider);
        Assert.StartsWith("mock-wa-", whatsApp.ProviderMessageId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryAttempt_IsRecorded_WithTheProvidersMessageIdAndTheMaskedNumber()
    {
        var (parcel, _, receiverPhone, _) = await BookAsync();
        await ProcessAllAsync(factory);

        var message = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms);
        var logs = await LogsAsync(factory, message.Id);

        var attempt = Assert.Single(logs);
        Assert.Equal(NotificationLogStatus.Submitted, attempt.Status);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(message.ProviderMessageId, attempt.ProviderMessageId);
        Assert.Equal("MockSms", attempt.Provider);
        Assert.DoesNotContain(receiverPhone, attempt.RecipientMasked, StringComparison.Ordinal);
        Assert.StartsWith(receiverPhone[..2], attempt.RecipientMasked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTestInbox_ShowsWhatWouldHaveBeenSentToAPhone()
    {
        var (parcel, _, _, _) = await BookAsync();
        await ProcessAllAsync(factory);
        var admin = await AdminAsync(factory);

        var inbox = await ReadAsync<NepalParcel.Contracts.Common.PagedResponse<MockProviderMessageResponse>>(
            await admin.GetAsync(new Uri("/api/v1/notifications/mock/messages?pageSize=100", UriKind.Relative)));

        var mine = inbox.Items.Where(m => m.Body.Contains(parcel.TrackingNumber, StringComparison.Ordinal)).ToList();
        Assert.Equal(4, mine.Count);
        Assert.Equal(2, mine.Count(m => m.Channel == "Sms"));
        var whatsApp = mine.First(m => m.Channel == "WhatsApp");
        Assert.Equal("afno_parcel_booked", whatsApp.TemplateName);
        Assert.Equal(2, whatsApp.Parameters.Count);
        Assert.Equal(parcel.TrackingNumber, whatsApp.Parameters[0]);
        Assert.All(mine, m => Assert.Matches(@"^\d\d\*+\d\d$", m.ToMasked));
    }

    // ---- reports from the provider -----------------------------------------------------------------------------------

    [Fact]
    public async Task ProviderReports_MoveAMessageForward_ThroughTheSameWebhookCodeARealProviderUses()
    {
        var (parcel, _, _, _) = await BookAsync();
        await ProcessAllAsync(factory);
        var admin = await AdminAsync(factory);
        var message = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms);

        Assert.Equal(1, (await SimulateAsync(admin, message.ProviderMessageId!, "sent")).Applied);
        var sent = await ReadAsync<NotificationMessageDetailResponse>(await admin.GetAsync(MessageUri(message.Id)));
        Assert.Equal("Sent", sent.Message.DeliveryStatus);
        Assert.Null(sent.Message.DeliveredAtUtc);

        await SimulateAsync(admin, message.ProviderMessageId!, "delivered");
        var delivered = await ReadAsync<NotificationMessageDetailResponse>(await admin.GetAsync(MessageUri(message.Id)));
        Assert.Equal("Delivered", delivered.Message.DeliveryStatus);
        Assert.NotNull(delivered.Message.DeliveredAtUtc);
        Assert.Equal("Submitted", delivered.Message.Status);

        await SimulateAsync(admin, message.ProviderMessageId!, "read");
        var read = await ReadAsync<NotificationMessageDetailResponse>(await admin.GetAsync(MessageUri(message.Id)));
        Assert.Equal("Read", read.Message.DeliveryStatus);
        Assert.NotNull(read.Message.ReadAtUtc);

        // Attempts and reports are one history: submitted, sent, delivered, read.
        Assert.Equal(["Submitted", "Sent", "Delivered", "Read"], read.Attempts.Select(a => a.Status).ToArray());
    }

    [Fact]
    public async Task ALateSentReportAfterDelivery_AndARepeatedReport_ChangeNothing()
    {
        var (parcel, _, _, _) = await BookAsync();
        await ProcessAllAsync(factory);
        var admin = await AdminAsync(factory);
        var message = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.WhatsApp);

        await SimulateAsync(admin, message.ProviderMessageId!, "delivered");
        await SimulateAsync(admin, message.ProviderMessageId!, "sent");
        var firstEvent = new MockWebhookRequest($"evt-{Guid.NewGuid():N}", message.ProviderMessageId!, "delivered");
        var first = await ReadAsync<WebhookAcceptedResponse>(await admin.PostAsJsonAsync(new Uri("/api/v1/webhooks/mock", UriKind.Relative), firstEvent));
        var again = await ReadAsync<WebhookAcceptedResponse>(await admin.PostAsJsonAsync(new Uri("/api/v1/webhooks/mock", UriKind.Relative), firstEvent));

        Assert.Equal(1, first.Applied);
        Assert.Equal(new WebhookAcceptedResponse(1, 0, 1, 0), again);
        var detail = await ReadAsync<NotificationMessageDetailResponse>(await admin.GetAsync(MessageUri(message.Id)));
        Assert.Equal("Delivered", detail.Message.DeliveryStatus);
        Assert.Null(detail.Message.SentAtUtc);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        Assert.Equal(1, await db.WebhookEvents.CountAsync(e => e.EventId == firstEvent.EventId));
    }

    [Fact]
    public async Task AFailureReport_IsRecordedAsFailed_AndTheMessageIsNotCountedAsDelivered()
    {
        var (parcel, _, _, _) = await BookAsync();
        await ProcessAllAsync(factory);
        var admin = await AdminAsync(factory);
        var message = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms);

        await SimulateAsync(admin, message.ProviderMessageId!, "failed", "Handset switched off");

        var detail = await ReadAsync<NotificationMessageDetailResponse>(await admin.GetAsync(MessageUri(message.Id)));
        Assert.Equal("Failed", detail.Message.DeliveryStatus);
        Assert.Equal("Handset switched off", detail.Message.DeliveryError);
        Assert.Null(detail.Message.DeliveredAtUtc);
        Assert.NotNull(detail.Message.FailedAtUtc);
        Assert.Equal("Submitted", detail.Message.Status);
    }

    [Fact]
    public async Task AReportThatArrivesBeforeWeKnowTheMessage_IsKept_AndAppliedWhenItAppears()
    {
        var (parcel, _, _, _) = await BookAsync();
        await ProcessAllAsync(factory);
        var admin = await AdminAsync(factory);
        var message = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms);
        var futureId = $"mock-sms-{Guid.NewGuid():N}";

        var early = await ReadAsync<WebhookAcceptedResponse>(
            await admin.PostAsJsonAsync(new Uri("/api/v1/webhooks/mock", UriKind.Relative), new MockWebhookRequest($"evt-{Guid.NewGuid():N}", futureId, "delivered")));
        Assert.Equal(new WebhookAcceptedResponse(1, 0, 0, 1), early);

        // The message now appears under that id, as if our save had landed after the provider's report.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
            var tracked = await db.NotificationOutbox.SingleAsync(m => m.Id == message.Id);
            db.Entry(tracked).Property(m => m.ProviderMessageId).CurrentValue = futureId;
            await db.SaveChangesAsync();
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var applied = await scope.ServiceProvider.GetRequiredService<INotificationWebhookService>().RetryPendingAsync();
            Assert.True(applied >= 1);
        }

        var detail = await ReadAsync<NotificationMessageDetailResponse>(await admin.GetAsync(MessageUri(message.Id)));
        Assert.Equal("Delivered", detail.Message.DeliveryStatus);
    }

    [Fact]
    public async Task OnlyAMessageTheMockAccepted_CanBeGivenASimulatedStatus()
    {
        var (parcel, _, _, _) = await BookAsync();
        var admin = await AdminAsync(factory);
        await ProcessAllAsync(factory);
        var message = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms);

        var unknown = await admin.PostAsJsonAsync(
            new Uri("/api/v1/notifications/mock/messages/mock-sms-doesnotexist/status", UriKind.Relative), new SimulateStatusRequest("delivered"));
        var bad = await admin.PostAsJsonAsync(
            new Uri($"/api/v1/notifications/mock/messages/{message.ProviderMessageId}/status", UriKind.Relative), new SimulateStatusRequest("teleported"));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ---- consent and opt-outs change what is sent --------------------------------------------------------------------

    [Fact]
    public async Task AnOptOutThatArrivesAfterQueueing_StopsTheMessage_WhenItIsAboutToBeSent()
    {
        var (parcel, _, receiverPhone, _) = await BookAsync();
        var admin = await AdminAsync(factory);
        (await admin.PostAsJsonAsync(new Uri("/api/v1/notifications/opt-outs", UriKind.Relative), new CreateOptOutRequest(receiverPhone, "WhatsApp"))).EnsureSuccessStatusCode();

        await ProcessAllAsync(factory);

        var whatsApp = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.WhatsApp);
        Assert.Equal(OutboxStatus.Skipped, whatsApp.Status);
        Assert.Contains("opted out", whatsApp.SkipReason, StringComparison.Ordinal);
        Assert.Null(whatsApp.ProviderMessageId);
        var logs = await LogsAsync(factory, whatsApp.Id);
        Assert.Equal(NotificationLogStatus.Skipped, Assert.Single(logs).Status);
        Assert.Equal(OutboxStatus.Submitted, (await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms)).Status);
    }

    [Fact]
    public async Task ConsentWithdrawnAfterQueueing_StopsTheSendersMessage_ButNotTheReceivers()
    {
        var (parcel, _, _, counter) = await BookAsync();
        var withdraw = await counter.PutAsJsonAsync(
            new Uri($"/api/v1/notifications/consent/parcel/{parcel.Id}", UriKind.Relative), new UpdateNotificationPreferencesRequest(Sms: false, WhatsApp: null));
        Assert.Equal(HttpStatusCode.OK, withdraw.StatusCode);
        var now = await ReadAsync<NotificationPreferencesResponse>(withdraw);
        Assert.False(now.Sms);
        Assert.True(now.WhatsApp);

        await ProcessAllAsync(factory);

        var senderSms = await MessageAsync(factory, parcel.Id, NotificationAudience.Sender, NotificationChannel.Sms);
        Assert.Equal(OutboxStatus.Skipped, senderSms.Status);
        Assert.Contains("no longer agrees", senderSms.SkipReason, StringComparison.Ordinal);
        Assert.Equal(OutboxStatus.Submitted, (await MessageAsync(factory, parcel.Id, NotificationAudience.Sender, NotificationChannel.WhatsApp)).Status);
        Assert.Equal(OutboxStatus.Submitted, (await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms)).Status);
    }

    // ---- the journey -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryStepOfTheJourney_SendsItsOwnMessage_ToBothPeopleOnBothChannels()
    {
        var (parcel, branch, _, counter) = await BookAsync();
        var warehouse = await _helpers.CreateWarehouseAsync(branch, "Kathmandu Hub");
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);
        var rider = await _helpers.StaffAsync(Roles.BranchManager);

        foreach (var response in new[]
        {
            await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("PickedUp", "Collected at the counter")),
            await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("Shipped", "Loaded on the truck", branchId: branch)),
            await hub.PostAsJsonAsync(StatusUri(parcel.Id), To("InTransit")),
            await hub.PostAsJsonAsync(StatusUri(parcel.Id), To("ArrivedAtWarehouse", "Sorted", warehouseId: warehouse)),
            await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("OutForDelivery", branchId: branch)),
            await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("Delivered", "Handed to the receiver")),
        })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var all = await MessagesAsync(factory, parcel.Id);
        var byTemplate = all.GroupBy(m => m.TemplateKey).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(
            new Dictionary<string, int>
            {
                [NotificationTemplateKeys.ParcelBooked] = 4,
                [NotificationTemplateKeys.ParcelPickedUp] = 4,
                [NotificationTemplateKeys.ParcelShipped] = 4,
                [NotificationTemplateKeys.ParcelInTransit] = 4,
                [NotificationTemplateKeys.ParcelAtWarehouse] = 4,
                [NotificationTemplateKeys.ParcelOutForDelivery] = 4,
                [NotificationTemplateKeys.ParcelDelivered] = 4,
            },
            byTemplate);
        Assert.Equal(all.Count, all.Select(m => (m.DedupeKey, m.Audience, m.Channel)).Distinct().Count());

        await ProcessAllAsync(factory);
        Assert.All(await MessagesAsync(factory, parcel.Id), m => Assert.Equal(OutboxStatus.Submitted, m.Status));
        Assert.All(await MessagesAsync(factory, parcel.Id), m => Assert.Equal(DeliveryStatus.None, m.DeliveryStatus));
    }

    [Fact]
    public async Task AFailedDeliveryAndAReturn_AreNotifiedToo()
    {
        var (parcel, branch, _, counter) = await BookAsync();
        var warehouse = await _helpers.CreateWarehouseAsync(branch, "Kathmandu Hub");
        var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);
        var rider = await _helpers.StaffAsync(Roles.BranchManager);
        await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("PickedUp"));
        await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("Shipped", "Loaded", branchId: branch));
        await hub.PostAsJsonAsync(StatusUri(parcel.Id), To("ArrivedAtWarehouse", "Sorted", warehouseId: warehouse));
        await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("OutForDelivery", branchId: branch));

        var failed = await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("DeliveryFailed", "Receiver was not at home"));
        var returned = await rider.PostAsJsonAsync(StatusUri(parcel.Id), To("Returned", "Sent back to the sender", branchId: branch));

        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        Assert.Equal(4, (await MessagesAsync(factory, parcel.Id, NotificationTemplateKeys.ParcelDeliveryFailed)).Count);
        Assert.Equal(4, (await MessagesAsync(factory, parcel.Id, NotificationTemplateKeys.ParcelReturned)).Count);

        // The reason a delivery failed is for staff: it never appears in what a person is sent.
        await ProcessAllAsync(factory);
        var failedSms = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms, NotificationTemplateKeys.ParcelDeliveryFailed);
        Assert.DoesNotContain("not at home", failedSms.RenderedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("not at home", failedSms.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedStatusChange_QueuesNothing()
    {
        var (parcel, _, _, counter) = await BookAsync();
        var before = (await MessagesAsync(factory, parcel.Id)).Count;

        var refused = await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("Delivered", "Jumping ahead"));

        Assert.False(refused.IsSuccessStatusCode);
        Assert.Equal(before, (await MessagesAsync(factory, parcel.Id)).Count);
    }

    [Fact]
    public async Task ACancelledBooking_SendsNothingFurther()
    {
        var (parcel, _, _, counter) = await BookAsync();
        var before = (await MessagesAsync(factory, parcel.Id)).Count;

        var cancel = await counter.PostAsJsonAsync(new Uri($"/api/v1/parcels/{parcel.Id}/cancel", UriKind.Relative), new CancelParcelRequest("Customer changed their mind"));

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(before, (await MessagesAsync(factory, parcel.Id)).Count);
    }

    // ---- one-time codes ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AOneTimeCode_IsSentByTheWorker_AndTheCodeIsErasedFromTheDatabaseAfterwards()
    {
        var (parcel, _, _, _) = await BookAsync(sms: null, whatsApp: null);
        (await factory.CreateClient().PostAsync(new Uri($"/api/v1/tracking/{parcel.TrackingNumber}/otp", UriKind.Relative), null)).EnsureSuccessStatusCode();
        var code = await _helpers.LatestOtpCodeAsync(parcel.Id);

        await ProcessAllAsync(factory);

        var message = Assert.Single(await MessagesAsync(factory, parcel.Id, NotificationTemplateKeys.TrackingOtp));
        Assert.Equal(OutboxStatus.Submitted, message.Status);
        Assert.Equal("MockSms", message.Provider);
        Assert.Equal("{}", message.PayloadJson);
        Assert.Null(message.RenderedBody);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var sent = await db.MockProviderMessages.AsNoTracking().Where(m => m.ProviderMessageId == message.ProviderMessageId).SingleAsync();
        Assert.Contains(code, sent.Body, StringComparison.Ordinal);
        Assert.Equal(NotificationChannel.Sms, sent.Channel);
    }

    [Fact]
    public async Task OneTimeCodes_NeedNoConsent_AndAreNeverSentOnWhatsApp()
    {
        var (parcel, _, receiverPhone, _) = await BookAsync(sms: false, whatsApp: false);
        (await factory.CreateClient().PostAsync(new Uri($"/api/v1/tracking/{parcel.TrackingNumber}/otp", UriKind.Relative), null)).EnsureSuccessStatusCode();

        var codes = await MessagesAsync(factory, parcel.Id, NotificationTemplateKeys.TrackingOtp);

        var code = Assert.Single(codes);
        Assert.Equal(NotificationChannel.Sms, code.Channel);
        Assert.Equal(receiverPhone, code.Recipient);
        Assert.Equal(OutboxStatus.Pending, code.Status);
    }
}
