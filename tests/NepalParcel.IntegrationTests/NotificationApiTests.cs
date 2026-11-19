using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Notifications;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Notifications;
using NepalParcel.Infrastructure.Persistence;
using NepalParcel.IntegrationTests.Infrastructure;
using static NepalParcel.IntegrationTests.Infrastructure.BookingTestHelpers;
using static NepalParcel.IntegrationTests.Infrastructure.NotificationTestSupport;

namespace NepalParcel.IntegrationTests;

/// <summary>Who may see and change what, the message texts, opt-outs, the customer's choices, and the provider webhooks.</summary>
public sealed class NotificationApiTests(NotificationApiFactory factory) : IClassFixture<NotificationApiFactory>
{
    private readonly BookingTestHelpers _helpers = new(factory);

    private static Uri Api(string path) => new($"/api/v1/notifications/{path}", UriKind.Relative);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<HttpClient> As(string role) => await TestUsers.CreateClientAsync(factory, role);

    private async Task<Guid> BookedParcelAsync()
    {
        var counter = await _helpers.StaffAsync(Roles.BookingStaff);
        var branch = await _helpers.CreateBranchAsync();
        return (await BookingTestHelpers.BookAsync(counter, NewBooking(branch, senderName: NewName("Sender"), receiverPhone: NewPhone()))).Id;
    }

    // ---- who may do what ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryNotificationEndpoint_NeedsASignIn()
    {
        var anonymous = factory.CreateClient();
        var id = Guid.NewGuid();

        foreach (var path in new[] { "messages", $"messages/{id}", "summary", "providers", "templates", "opt-outs", "preferences", "mock/messages", $"consent/parcel/{id}" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Api(path))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri($"/api/v1/parcels/{id}/notifications", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Api($"messages/{id}/requeue"), new RequeueNotificationRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(new Uri("/api/v1/webhooks/mock", UriKind.Relative), new MockWebhookRequest("e", "m", "sent"))).StatusCode);
    }

    [Theory]
    [InlineData(Roles.Customer)]
    [InlineData(Roles.BookingStaff)]
    [InlineData(Roles.WarehouseStaff)]
    [InlineData(Roles.DeliveryAgent)]
    [InlineData(Roles.Accountant)]
    public async Task OnlyAdministratorsAndBranchManagers_CanReadTheMessagesTheirTextsAndTheOptOuts(string role)
    {
        var client = await As(role);

        foreach (var path in new[] { "messages", "summary", "providers", "templates", "opt-outs" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Api(path))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Api("mock/messages"))).StatusCode);
    }

    [Fact]
    public async Task ABranchManager_CanRead_ButOnlyTheSuperAdministratorCanChange()
    {
        var manager = await As(Roles.BranchManager);
        var admin = await As(Roles.SuperAdmin);
        var templates = await ReadAsync<NotificationTemplatesResponse>(await admin.GetAsync(Api("templates")));
        var template = templates.Templates.First();

        foreach (var path in new[] { "messages", "summary", "providers", "templates", "opt-outs" })
        {
            Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(Api(path))).StatusCode);
        }

        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(Api($"messages/{id}/requeue"), new RequeueNotificationRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(Api($"messages/{id}/cancel"), new RequeueNotificationRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync(Api($"templates/{template.Id}"), new UpdateNotificationTemplateRequest("x {tracking}", template.ProviderTemplateName, true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(Api("opt-outs"), new CreateOptOutRequest("9841234567", "Sms"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync(Api($"opt-outs/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(Api("mock/messages"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(Api("mock/messages/x/status"), new SimulateStatusRequest("sent"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(new Uri("/api/v1/webhooks/mock", UriKind.Relative), new MockWebhookRequest("e", "m", "sent"))).StatusCode);
    }

    [Fact]
    public async Task AParcelsMessages_AreForStaffWhoReadParcels()
    {
        var parcelId = await BookedParcelAsync();
        var uri = new Uri($"/api/v1/parcels/{parcelId}/notifications", UriKind.Relative);

        foreach (var role in new[] { Roles.SuperAdmin, Roles.BranchManager, Roles.BookingStaff, Roles.WarehouseStaff, Roles.Accountant })
        {
            var list = await ReadAsync<List<NotificationMessageResponse>>(await (await As(role)).GetAsync(uri));
            Assert.Equal(4, list.Count);
            Assert.All(list, m => Assert.Matches(@"^\d\d\*+\d\d$", m.RecipientMasked));
            Assert.All(list, m => Assert.NotNull(m.TrackingNumber));
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(Roles.Customer)).GetAsync(uri)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As(Roles.DeliveryAgent)).GetAsync(uri)).StatusCode);
    }

    [Fact]
    public async Task NoMessageEverShowsAFullPhoneNumberOrACode()
    {
        var parcelId = await BookedParcelAsync();
        await ProcessAllAsync(factory);
        var admin = await As(Roles.SuperAdmin);

        var body = await (await admin.GetAsync(Api($"messages?parcelId={parcelId}&pageSize=100"))).Content.ReadAsStringAsync();
        var page = JsonSerializer.Deserialize<PagedResponse<NotificationMessageResponse>>(body, JsonSerializerOptions.Web)!;

        Assert.Equal(4, page.TotalCount);
        Assert.DoesNotContain("9841234567", body, StringComparison.Ordinal);
        Assert.DoesNotContain("payloadJson", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recipient\"", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- listing and filtering ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMessageList_CanBeFilteredByStatusChannelAudienceAndTrackingNumber()
    {
        var parcelId = await BookedParcelAsync();
        await ProcessAllAsync(factory);
        var admin = await As(Roles.SuperAdmin);
        var tracking = (await ReadAsync<List<NotificationMessageResponse>>(await admin.GetAsync(new Uri($"/api/v1/parcels/{parcelId}/notifications", UriKind.Relative)))).First().TrackingNumber!;

        var all = await ReadAsync<PagedResponse<NotificationMessageResponse>>(await admin.GetAsync(Api($"messages?search={tracking[..8]}&pageSize=100")));
        var whatsApp = await ReadAsync<PagedResponse<NotificationMessageResponse>>(await admin.GetAsync(Api($"messages?parcelId={parcelId}&channel=WhatsApp")));
        var senders = await ReadAsync<PagedResponse<NotificationMessageResponse>>(await admin.GetAsync(Api($"messages?parcelId={parcelId}&audience=Sender")));
        var submitted = await ReadAsync<PagedResponse<NotificationMessageResponse>>(await admin.GetAsync(Api($"messages?parcelId={parcelId}&status=Submitted&deliveryStatus=None")));
        var delivered = await ReadAsync<PagedResponse<NotificationMessageResponse>>(await admin.GetAsync(Api($"messages?parcelId={parcelId}&deliveryStatus=Delivered")));
        var template = await ReadAsync<PagedResponse<NotificationMessageResponse>>(await admin.GetAsync(Api($"messages?parcelId={parcelId}&templateKey=parcel-booked")));

        Assert.True(all.TotalCount >= 4);
        Assert.Equal(2, whatsApp.TotalCount);
        Assert.All(whatsApp.Items, m => Assert.Equal("WhatsApp", m.Channel));
        Assert.Equal(2, senders.TotalCount);
        // The sender never agreed, so only the receiver's two messages were sent.
        Assert.Equal(2, submitted.TotalCount);
        Assert.Equal(0, delivered.TotalCount);
        Assert.Equal(4, template.TotalCount);
    }

    [Fact]
    public async Task ABadFilter_IsRefusedWithAMessage_NotIgnored()
    {
        var admin = await As(Roles.SuperAdmin);

        foreach (var query in new[] { "status=Teleported", "channel=Email", "deliveryStatus=Maybe", "audience=Everyone", "pageSize=0", "pageSize=500", "page=0", "from=2026-02-01&to=2026-01-01" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(Api($"messages?{query}"))).StatusCode);
        }
    }

    [Fact]
    public async Task AMessageThatDoesNotExist_IsNotFound()
    {
        var admin = await As(Roles.SuperAdmin);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(Api($"messages/{Guid.NewGuid()}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(Api($"messages/{Guid.NewGuid()}/requeue"), new RequeueNotificationRequest())).StatusCode);
    }

    [Fact]
    public async Task TheSummary_CountsByChannelAndWhatHappened_AndNeverCallsAnAcceptedMessageDelivered()
    {
        await BookedParcelAsync();
        await ProcessAllAsync(factory);
        var admin = await As(Roles.SuperAdmin);

        var summary = await ReadAsync<NotificationSummaryResponse>(await admin.GetAsync(Api("summary")));

        Assert.Equal(["Sms", "WhatsApp"], summary.Channels.Select(c => c.Channel).ToArray());
        var sms = summary.Channels.Single(c => c.Channel == "Sms");
        Assert.True(sms.Total >= 2);
        Assert.True(sms.Submitted >= 1);
        Assert.Equal(0, sms.Delivered);
        Assert.Equal(0, sms.Read);
        Assert.True(sms.Skipped >= 1);
        Assert.Equal(sms.Total, sms.Queued + sms.Retrying + sms.Submitted + sms.Sent + sms.Delivered + sms.Failed + sms.Skipped);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(Api("summary?from=2026-03-01&to=2026-01-01"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(Api("summary?from=2020-01-01&to=2026-01-01"))).StatusCode);
    }

    [Fact]
    public async Task TheProviderList_SaysMockPlainly_AndNeverShowsCredentials()
    {
        var admin = await As(Roles.SuperAdmin);

        var response = await admin.GetAsync(Api("providers"));
        var body = await response.Content.ReadAsStringAsync();
        var providers = JsonSerializer.Deserialize<List<NotificationProviderInfo>>(body, JsonSerializerOptions.Web)!;

        Assert.Equal(["Sms", "WhatsApp"], providers.Select(p => p.Channel).Order().ToArray());
        Assert.All(providers, p => Assert.True(p.IsMock));
        Assert.All(providers, p => Assert.True(p.IsConfigured));
        Assert.DoesNotContain(NotificationApiFactory.AppSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(NotificationApiFactory.VerifyToken, body, StringComparison.Ordinal);
    }

    // ---- templates ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryMessageHasAnSmsText_AndTheParcelEventsAlsoHaveAWhatsAppTemplate()
    {
        var admin = await As(Roles.SuperAdmin);

        var response = await ReadAsync<NotificationTemplatesResponse>(await admin.GetAsync(Api("templates")));

        Assert.Equal(20, response.Templates.Count);
        Assert.Equal(11, response.Templates.Count(t => t.Channel == "Sms"));
        Assert.Equal(9, response.Templates.Count(t => t.Channel == "WhatsApp"));
        Assert.All(response.Templates.Where(t => t.Channel == "WhatsApp"), t => Assert.False(string.IsNullOrWhiteSpace(t.ProviderTemplateName)));
        Assert.All(response.Templates, t => Assert.True(t.IsActive));
        Assert.Contains("link", response.AllowedPlaceholders["parcel-booked"]);
        Assert.DoesNotContain("reason", response.AllowedPlaceholders["parcel-delivery-failed"]);
        Assert.Equal(["code", "minutes", "tracking"], response.AllowedPlaceholders["tracking-otp"].Order().ToArray());
        Assert.Equal("parcel-booked", response.Templates[0].Key);
    }

    [Fact]
    public async Task ATextCanBeEdited_ButOnlyWithPlaceholdersThatExist()
    {
        var admin = await As(Roles.SuperAdmin);
        var sms = (await ReadAsync<NotificationTemplatesResponse>(await admin.GetAsync(Api("templates")))).Templates
            .Single(t => t.Key == "parcel-picked-up" && t.Channel == "Sms");

        var unknown = await admin.PutAsJsonAsync(Api($"templates/{sms.Id}"), new UpdateNotificationTemplateRequest("Hello {receiverName}", null, true));
        var stray = await admin.PutAsJsonAsync(Api($"templates/{sms.Id}"), new UpdateNotificationTemplateRequest("Parcel {tracking", null, true));
        var empty = await admin.PutAsJsonAsync(Api($"templates/{sms.Id}"), new UpdateNotificationTemplateRequest(" ", null, true));
        var ok = await admin.PutAsJsonAsync(Api($"templates/{sms.Id}"), new UpdateNotificationTemplateRequest("Picked up: {tracking}. {link}", null, true));

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("receiverName", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, stray.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var saved = await ReadAsync<NotificationTemplateResponse>(ok);
        Assert.Equal("Picked up: {tracking}. {link}", saved.Body);
        Assert.Equal(["tracking", "link"], saved.Parameters.ToArray());

        var counter = await As(Roles.BookingStaff);
        var branch = await _helpers.CreateBranchAsync();
        var parcel = await BookingTestHelpers.BookAsync(counter, NewBooking(branch, senderName: NewName("Sender"), receiverPhone: NewPhone()));
        (await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("PickedUp"))).EnsureSuccessStatusCode();
        await ProcessAllAsync(factory);
        var sent = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms, NotificationTemplateKeys.ParcelPickedUp);
        Assert.StartsWith($"Picked up: {parcel.TrackingNumber}. ", sent.RenderedBody, StringComparison.Ordinal);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "NotificationTemplate" && a.EntityId == sms.Id.ToString()));
    }

    [Fact]
    public async Task AnActiveWhatsAppMessage_NeedsTheNameOfTheTemplateMetaApproved()
    {
        var admin = await As(Roles.SuperAdmin);
        var whatsApp = (await ReadAsync<NotificationTemplatesResponse>(await admin.GetAsync(Api("templates")))).Templates
            .Single(t => t.Key == "parcel-returned" && t.Channel == "WhatsApp");

        var unnamed = await admin.PutAsJsonAsync(Api($"templates/{whatsApp.Id}"), new UpdateNotificationTemplateRequest(whatsApp.Body, " ", true));
        var badName = await admin.PutAsJsonAsync(Api($"templates/{whatsApp.Id}"), new UpdateNotificationTemplateRequest(whatsApp.Body, "Has Capitals", true));
        var switchedOff = await admin.PutAsJsonAsync(Api($"templates/{whatsApp.Id}"), new UpdateNotificationTemplateRequest(whatsApp.Body, null, false));

        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        var off = await ReadAsync<NotificationTemplateResponse>(switchedOff);
        Assert.False(off.IsActive);

        // Switch it back on, so the other tests find it.
        var restored = await admin.PutAsJsonAsync(Api($"templates/{whatsApp.Id}"), new UpdateNotificationTemplateRequest(whatsApp.Body, whatsApp.ProviderTemplateName, true));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [Fact]
    public async Task AnInactiveTemplate_MeansTheMessageIsRecordedAsSkipped()
    {
        var admin = await As(Roles.SuperAdmin);
        var template = (await ReadAsync<NotificationTemplatesResponse>(await admin.GetAsync(Api("templates")))).Templates
            .Single(t => t.Key == "parcel-in-transit" && t.Channel == "Sms");
        (await admin.PutAsJsonAsync(Api($"templates/{template.Id}"), new UpdateNotificationTemplateRequest(template.Body, null, false))).EnsureSuccessStatusCode();
        try
        {
            var counter = await As(Roles.BookingStaff);
            var branch = await _helpers.CreateBranchAsync();
            var parcel = await BookingTestHelpers.BookAsync(counter, NewBooking(branch, senderName: NewName("Sender"), receiverPhone: NewPhone()));
            (await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("PickedUp"))).EnsureSuccessStatusCode();
            (await counter.PostAsJsonAsync(StatusUri(parcel.Id), To("Shipped", "Loaded", branchId: branch))).EnsureSuccessStatusCode();
            var hub = await _helpers.StaffAtAsync(Roles.WarehouseStaff, branch);
            (await hub.PostAsJsonAsync(StatusUri(parcel.Id), To("InTransit"))).EnsureSuccessStatusCode();

            var sms = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms, NotificationTemplateKeys.ParcelInTransit);
            var whatsApp = await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.WhatsApp, NotificationTemplateKeys.ParcelInTransit);

            Assert.Equal(OutboxStatus.Skipped, sms.Status);
            Assert.Contains("template", sms.SkipReason, StringComparison.Ordinal);
            Assert.Equal(OutboxStatus.Pending, whatsApp.Status);
        }
        finally
        {
            await admin.PutAsJsonAsync(Api($"templates/{template.Id}"), new UpdateNotificationTemplateRequest(template.Body, null, true));
        }
    }

    [Fact]
    public async Task ThePreview_FillsInSamples_AndSaysWhatIsWrong_WithoutSavingAnything()
    {
        var manager = await As(Roles.BranchManager);

        var good = await ReadAsync<PreviewTemplateResponse>(await manager.PostAsJsonAsync(Api("templates/preview"), new PreviewTemplateRequest("parcel-shipped", "Parcel {tracking} {link}")));
        var bad = await ReadAsync<PreviewTemplateResponse>(await manager.PostAsJsonAsync(Api("templates/preview"), new PreviewTemplateRequest("parcel-shipped", "Parcel {tracking} {phone}")));
        var unknownKey = await manager.PostAsJsonAsync(Api("templates/preview"), new PreviewTemplateRequest("parcel-teleported", "x"));

        Assert.Empty(good.Problems);
        Assert.Equal(["tracking", "link"], good.Parameters.ToArray());
        Assert.StartsWith("Parcel NP-", good.Text, StringComparison.Ordinal);
        Assert.Contains(bad.Problems, p => p.Contains("phone", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, unknownKey.StatusCode);
    }

    // ---- opt-outs ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnOptOut_CanBeRecordedListedAndLifted_AndTheHistoryIsKept()
    {
        var admin = await As(Roles.SuperAdmin);
        var phone = NewPhone();

        var created = await admin.PostAsJsonAsync(Api("opt-outs"), new CreateOptOutRequest(phone, "WhatsApp"));
        var duplicate = await admin.PostAsJsonAsync(Api("opt-outs"), new CreateOptOutRequest(phone, "whatsapp"));
        var otherChannel = await admin.PostAsJsonAsync(Api("opt-outs"), new CreateOptOutRequest(phone, "Sms"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var optOut = (await created.Content.ReadFromJsonAsync<OptOutResponse>())!;
        Assert.Matches(@"^98\*+\d\d$", optOut.PhoneMasked);
        Assert.Equal("Staff", optOut.Source);
        Assert.Null(optOut.RemovedAtUtc);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otherChannel.StatusCode);

        var active = await ReadAsync<PagedResponse<OptOutResponse>>(await admin.GetAsync(Api("opt-outs?pageSize=100")));
        Assert.Contains(active.Items, o => o.Id == optOut.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Api($"opt-outs/{optOut.Id}"))).StatusCode);
        var afterActive = await ReadAsync<PagedResponse<OptOutResponse>>(await admin.GetAsync(Api("opt-outs?pageSize=100")));
        var afterAll = await ReadAsync<PagedResponse<OptOutResponse>>(await admin.GetAsync(Api("opt-outs?pageSize=100&activeOnly=false")));
        Assert.DoesNotContain(afterActive.Items, o => o.Id == optOut.Id);
        Assert.NotNull(afterAll.Items.Single(o => o.Id == optOut.Id).RemovedAtUtc);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.DeleteAsync(Api($"opt-outs/{optOut.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync(Api($"opt-outs/{Guid.NewGuid()}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(Api("opt-outs"), new CreateOptOutRequest(phone, "WhatsApp"))).StatusCode);
    }

    [Theory]
    [InlineData("12345", "Sms")]
    [InlineData("", "Sms")]
    [InlineData("9841234567", "Email")]
    [InlineData("9841234567", "")]
    public async Task AnOptOut_NeedsARealNumberAndAMessagingChannel(string phone, string channel)
    {
        var admin = await As(Roles.SuperAdmin);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Api("opt-outs"), new CreateOptOutRequest(phone, channel))).StatusCode);
    }

    // ---- the customer's own choices ----------------------------------------------------------------------------------

    [Fact]
    public async Task ACustomerStartsWithBothChannelsOff_AndCanTurnThemOnAndOff()
    {
        var customer = await _helpers.CustomerAsync();

        var start = await ReadAsync<NotificationPreferencesResponse>(await customer.GetAsync(Api("preferences")));
        var smsOn = await ReadAsync<NotificationPreferencesResponse>(await customer.PutAsJsonAsync(Api("preferences"), new UpdateNotificationPreferencesRequest(true, null)));
        var bothOn = await ReadAsync<NotificationPreferencesResponse>(await customer.PutAsJsonAsync(Api("preferences"), new UpdateNotificationPreferencesRequest(null, true)));
        var smsOff = await ReadAsync<NotificationPreferencesResponse>(await customer.PutAsJsonAsync(Api("preferences"), new UpdateNotificationPreferencesRequest(false, null)));
        var unchanged = await ReadAsync<NotificationPreferencesResponse>(await customer.PutAsJsonAsync(Api("preferences"), new UpdateNotificationPreferencesRequest(false, true)));

        Assert.False(start.Sms);
        Assert.False(start.WhatsApp);
        Assert.Null(start.SmsDecidedAtUtc);
        Assert.True(smsOn.Sms);
        Assert.False(smsOn.WhatsApp);
        Assert.NotNull(smsOn.SmsDecidedAtUtc);
        Assert.True(bothOn.Sms);
        Assert.True(bothOn.WhatsApp);
        Assert.False(smsOff.Sms);
        Assert.True(smsOff.WhatsApp);
        Assert.Equal(smsOff, unchanged);

        // Every change is a new record: nothing was edited or deleted.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var customerId = (await db.Customers.AsNoTracking().OrderByDescending(c => c.Id).Take(50).ToListAsync())
            .First(c => db.CustomerConsents.Any(x => x.CustomerId == c.Id && x.Type == ConsentType.SmsNotifications)).Id;
        var history = await db.CustomerConsents.AsNoTracking().Where(c => c.CustomerId == customerId).OrderBy(c => c.RecordedAtUtc).ThenBy(c => c.Id).ToListAsync();
        Assert.Equal(
            [(ConsentType.SmsNotifications, true), (ConsentType.WhatsAppNotifications, true), (ConsentType.SmsNotifications, false)],
            history.Select(h => (h.Type, h.IsGranted)).ToArray());
        Assert.All(history, h => Assert.Equal(ConsentSource.Web, h.Source));
    }

    [Fact]
    public async Task APreferencesChange_MustChangeSomething_AndIsOnlyForCustomers()
    {
        var customer = await _helpers.CustomerAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PutAsJsonAsync(Api("preferences"), new UpdateNotificationPreferencesRequest(null, null))).StatusCode);
        foreach (var role in new[] { Roles.BookingStaff, Roles.SuperAdmin, Roles.BranchManager })
        {
            var staff = await As(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync(Api("preferences"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutAsJsonAsync(Api("preferences"), new UpdateNotificationPreferencesRequest(true, true))).StatusCode);
        }
    }

    [Fact]
    public async Task OnlyCounterStaff_RecordWhatASenderSaidAtTheDesk()
    {
        var parcelId = await BookedParcelAsync();
        var uri = Api($"consent/parcel/{parcelId}");

        var counter = await As(Roles.BookingStaff);
        var before = await ReadAsync<NotificationPreferencesResponse>(await counter.GetAsync(uri));
        var after = await ReadAsync<NotificationPreferencesResponse>(await counter.PutAsJsonAsync(uri, new UpdateNotificationPreferencesRequest(true, true)));

        Assert.False(before.Sms);
        Assert.True(after.Sms);
        Assert.True(after.WhatsApp);
        foreach (var role in new[] { Roles.Customer, Roles.WarehouseStaff, Roles.Accountant, Roles.DeliveryAgent })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await (await As(role)).PutAsJsonAsync(uri, new UpdateNotificationPreferencesRequest(true, true))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await counter.GetAsync(Api($"consent/parcel/{Guid.NewGuid()}"))).StatusCode);
    }

    [Fact]
    public async Task ABookingByACustomerWhoTurnedUpdatesOn_QueuesTheirMessagesStraightAway()
    {
        var customer = await _helpers.CustomerAsync();
        (await customer.PutAsJsonAsync(Api("preferences"), new UpdateNotificationPreferencesRequest(true, true))).EnsureSuccessStatusCode();
        var branch = await _helpers.CreateBranchAsync();

        var parcel = await BookingTestHelpers.BookAsync(customer, NewBooking(branch, receiverPhone: NewPhone()));

        var sender = await MessageAsync(factory, parcel.Id, NotificationAudience.Sender, NotificationChannel.Sms);
        Assert.Equal(OutboxStatus.Pending, sender.Status);
        Assert.Equal(OutboxStatus.Pending, (await MessageAsync(factory, parcel.Id, NotificationAudience.Sender, NotificationChannel.WhatsApp)).Status);
    }

    // ---- webhooks ----------------------------------------------------------------------------------------------------

    private static string StopBody(string messageId, string from, string text) =>
        JsonSerializer.Serialize(new
        {
            @object = "whatsapp_business_account",
            entry = new[] { new { id = "1", changes = new[] { new { field = "messages", value = new { messaging_product = "whatsapp", messages = new[] { new { from, id = messageId, timestamp = "1795000000", type = "text", text = new { body = text } } } } } } } },
        });

    private static string StatusBody(string messageId, string status) =>
        JsonSerializer.Serialize(new
        {
            @object = "whatsapp_business_account",
            entry = new[] { new { id = "1", changes = new[] { new { field = "messages", value = new { messaging_product = "whatsapp", statuses = new[] { new { id = messageId, status, timestamp = "1795000000", recipient_id = "9779851234567" } } } } } } },
        });

    [Fact]
    public async Task MetasEndpointCheck_IsAnsweredWithTheChallenge_OnlyForTheRightToken()
    {
        var client = factory.CreateClient();

        var ok = await client.GetAsync(new Uri($"/api/v1/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token={NotificationApiFactory.VerifyToken}&hub.challenge=1158201444", UriKind.Relative));
        var wrongToken = await client.GetAsync(new Uri("/api/v1/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=nope&hub.challenge=1158201444", UriKind.Relative));
        var wrongMode = await client.GetAsync(new Uri($"/api/v1/webhooks/whatsapp?hub.mode=unsubscribe&hub.verify_token={NotificationApiFactory.VerifyToken}&hub.challenge=1", UriKind.Relative));
        var nothing = await client.GetAsync(new Uri("/api/v1/webhooks/whatsapp", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("1158201444", await ok.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", ok.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.Forbidden, wrongToken.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongMode.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, nothing.StatusCode);
    }

    [Fact]
    public async Task AWebhookWithoutAValidSignature_IsRefused_AndChangesNothing()
    {
        var client = factory.CreateClient();
        var phone = NewPhone();
        var body = StopBody($"wamid.{Guid.NewGuid():N}", $"977{phone}", "STOP");

        var missing = await PostMetaAsync(client, body, null);
        var wrong = await PostMetaAsync(client, body, MetaSignature(body, "another-secret"));
        var tampered = await PostMetaAsync(client, body.Replace("STOP", "stop ", StringComparison.Ordinal), MetaSignature(body));
        var malformed = await PostMetaAsync(client, body, "sha256=zzzz");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        Assert.False(await db.NotificationOptOuts.AnyAsync(o => o.PhoneNumber == phone));
        Assert.False(await db.WebhookEvents.AnyAsync(e => e.Provider == "MetaWhatsApp" && e.PayloadJson.Contains(phone)));
    }

    [Fact]
    public async Task ReplyingStopOnWhatsApp_OptsTheNumberOut_AndReplyingStartLiftsIt()
    {
        var client = factory.CreateClient();
        var phone = NewPhone();
        var stop = StopBody($"wamid.{Guid.NewGuid():N}", $"977{phone}", "STOP");
        var start = StopBody($"wamid.{Guid.NewGuid():N}", $"977{phone}", "START");

        var stopped = await PostMetaAsync(client, stop, MetaSignature(stop));
        var repeated = await PostMetaAsync(client, stop, MetaSignature(stop));

        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        Assert.Equal(new WebhookAcceptedResponse(1, 1, 0, 0), await stopped.Content.ReadFromJsonAsync<WebhookAcceptedResponse>());
        Assert.Equal(new WebhookAcceptedResponse(1, 0, 1, 0), await repeated.Content.ReadFromJsonAsync<WebhookAcceptedResponse>());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
            var optOut = await db.NotificationOptOuts.AsNoTracking().SingleAsync(o => o.PhoneNumber == phone);
            Assert.Equal(NotificationChannel.WhatsApp, optOut.Channel);
            Assert.Equal(OptOutSource.InboundMessage, optOut.Source);
            Assert.True(optOut.IsActive);
        }

        // A booking now skips WhatsApp for that number but not SMS.
        var counter = await As(Roles.BookingStaff);
        var branch = await _helpers.CreateBranchAsync();
        var parcel = await BookingTestHelpers.BookAsync(counter, NewBooking(branch, senderName: NewName("Sender"), receiverPhone: phone));
        Assert.Equal(OutboxStatus.Skipped, (await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.WhatsApp)).Status);
        Assert.Equal(OutboxStatus.Pending, (await MessageAsync(factory, parcel.Id, NotificationAudience.Receiver, NotificationChannel.Sms)).Status);

        var started = await PostMetaAsync(client, start, MetaSignature(start));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        await using var scope2 = factory.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var lifted = await db2.NotificationOptOuts.AsNoTracking().SingleAsync(o => o.PhoneNumber == phone);
        Assert.False(lifted.IsActive);
    }

    [Fact]
    public async Task AMetaDeliveryReportForAnUnknownMessage_IsAcceptedAndKeptForLater()
    {
        var client = factory.CreateClient();
        var id = $"wamid.{Guid.NewGuid():N}";
        var body = StatusBody(id, "delivered");

        var first = await PostMetaAsync(client, body, MetaSignature(body));
        var again = await PostMetaAsync(client, body, MetaSignature(body));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(new WebhookAcceptedResponse(1, 0, 0, 1), await first.Content.ReadFromJsonAsync<WebhookAcceptedResponse>());
        Assert.Equal(new WebhookAcceptedResponse(1, 0, 1, 0), await again.Content.ReadFromJsonAsync<WebhookAcceptedResponse>());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var stored = await db.WebhookEvents.AsNoTracking().SingleAsync(e => e.Provider == "MetaWhatsApp" && e.EventId == $"{id}:delivered");
        Assert.Equal(NepalParcel.Domain.Integrations.WebhookStatus.Received, stored.Status);
        Assert.True(stored.SignatureValid);
    }

    [Fact]
    public async Task ABodyThatIsNotJson_WithAValidSignature_IsABadRequest()
    {
        var body = "this is not json";

        var response = await PostMetaAsync(factory.CreateClient(), body, MetaSignature(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- the dedupe guarantee ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSameEvent_CannotBeQueuedTwice_ForTheSameAudienceAndChannel()
    {
        var parcelId = await BookedParcelAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var existing = await db.NotificationOutbox.AsNoTracking().FirstAsync(m => m.ParcelId == parcelId && m.DedupeKey != null);

        db.NotificationOutbox.Add(NotificationOutboxMessage.EnqueueForParcel(
            existing.Channel, existing.Audience, existing.Recipient, existing.TemplateKey, "{}", DateTimeOffset.UtcNow, parcelId, existing.CustomerId, existing.DedupeKey!, 5));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AProviderMessageId_BelongsToOneMessageOnly()
    {
        var parcelId = await BookedParcelAsync();
        await ProcessAllAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NepalParcelDbContext>();
        var messages = await db.NotificationOutbox.Where(m => m.ParcelId == parcelId && m.ProviderMessageId != null).ToListAsync();
        Assert.Equal(2, messages.Count);

        db.Entry(messages[1]).Property(m => m.ProviderMessageId).CurrentValue = messages[0].ProviderMessageId;
        db.Entry(messages[1]).Property(m => m.Provider).CurrentValue = messages[0].Provider;

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
